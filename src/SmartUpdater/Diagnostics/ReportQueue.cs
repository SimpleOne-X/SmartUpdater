using System.Text;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 上报的补报队列，落在 <c>.smartupdater/reports.jsonl</c>（JSON Lines：每行一条 <see cref="UpdateReport"/>，UTF-8 无 BOM，行尾 <c>\n</c>）。
/// 传输失败的上报入队，下次轮询时逐条补发。每次修改都是"读全部 → 内存修改 → 原子整体重写"，
/// 队列上限 <see cref="MaxEntries"/> 条，重写成本可忽略，换来文件永不出现半行。
/// 进程内用信号量串行化读写；跨进程不加锁——按设计同一时刻只有一个进程会碰这个文件。
/// </summary>
internal sealed class ReportQueue
{
    /// <summary>队列条数上限。超出时淘汰最旧的。</summary>
    public const int MaxEntries = 100;

    /// <summary>条目最长保留时间。<c>now - ReportedAt</c> 严格大于它的条目在读取时丢弃。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly IFileOperations _fs;
    private readonly TimeProvider _time;
    private readonly IUpdateLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>创建队列。文件与其目录都可以尚不存在。</summary>
    public ReportQueue(string filePath, IFileOperations fs, TimeProvider timeProvider, IUpdateLog log)
    {
        _filePath = filePath;
        _fs = fs;
        _time = timeProvider;
        _log = log;
    }

    /// <summary>从磁盘读出待发送的记录（最旧在前）；跳过损坏行、剪掉过期条目；不改文件。</summary>
    public IReadOnlyList<UpdateReport> Peek()
    {
        _gate.Wait();
        try
        {
            return Load();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>追加一条：读 → 追加 → 剪过期 → 超过 <see cref="MaxEntries"/> 时淘汰最旧 → 原子重写。写失败的异常原样抛出。</summary>
    public void Enqueue(UpdateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _gate.Wait();
        try
        {
            List<UpdateReport> list = Load();
            list.Add(report);
            DropExpired(list);

            int evicted = list.Count - MaxEntries;
            if (evicted > 0)
            {
                list.RemoveRange(0, evicted);
                _log.Warning(null, $"上报队列已满，淘汰最旧的 {evicted} 条");
            }

            Save(list);
            _log.Information(null, $"上报入队：{report.EventType}，队列深度 {list.Count}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 逐条调用 <paramref name="send"/>（最旧在前）。返回 true → 出队并立即重写文件；返回 false → 留队、继续下一条；
    /// 抛异常 → 留队、本轮停止。<paramref name="send"/> 执行期间不持锁，<see cref="Enqueue"/> 不会被它卡住。
    /// 返回成功发送的条数。取消 → 抛 <see cref="OperationCanceledException"/>，队列不变。
    /// </summary>
    /// <remarks>
    /// 两种失败含义不同：抛异常几乎总是传输失败（断网、服务端重启），后面的条目大概率也失败，继续试只会把每轮拖成 N × 超时，所以本轮停止；
    /// 返回 false 是服务端可达但没接受这一条，继续下一条代价很小，且避免一条"毒药记录"堵住后面的条目整整 7 天。
    /// 每成功一条就重写文件：进程在两条之间被杀最多导致一条重发——上报本就是至少一次语义。
    /// 不是单飞：两次并发的 <see cref="FlushAsync"/> 会重复发送同一批，调用方不得重叠调用；<see cref="Dequeue"/> 内保存失败的 IOException 会向上传播并丢失已发送计数（至少一次语义不变）。
    /// </remarks>
    public async Task<int> FlushAsync(Func<UpdateReport, CancellationToken, Task<bool>> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        IReadOnlyList<UpdateReport> pending = Peek();
        int sentCount = 0;

        foreach (UpdateReport report in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool accepted;
            try
            {
                accepted = await send(report, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(null, $"上报发送失败（异常），留在队列，本轮停止：{report.EventType}", ex);
                break;
            }

            if (!accepted)
            {
                _log.Warning(null, $"上报未被接受（send 返回 false），留在队列，继续下一条：{report.EventType}");
                continue;
            }

            int remaining = Dequeue(report);
            sentCount++;
            _log.Information(null, $"上报发送成功：{report.EventType}，队列深度 {remaining}");
        }

        return sentCount;
    }

    /// <summary>把已成功发送的条目从磁盘队列移除，返回移除后的队列深度。按"序列化后 JSON 相同的第一行"匹配。</summary>
    private int Dequeue(UpdateReport sent)
    {
        _gate.Wait();
        try
        {
            List<UpdateReport> list = Load();
            string key = Serialize(sent);
            int index = list.FindIndex(r => string.Equals(Serialize(r), key, StringComparison.Ordinal));
            if (index >= 0)
            {
                list.RemoveAt(index);
                Save(list);
            }

            return list.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>调用方必须持锁。文件不存在返回空列表；损坏行跳过并记 Warning；过期条目丢弃。</summary>
    private List<UpdateReport> Load()
    {
        byte[]? bytes = AtomicFile.ReadAllBytes(_fs, _filePath);
        var list = new List<UpdateReport>();
        if (bytes is null)
        {
            return list;
        }

        string[] lines = Encoding.UTF8.GetString(AtomicFile.SkipUtf8Bom(bytes)).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            int lineNumber = i + 1;
            UpdateReport? report;
            try
            {
                report = JsonSerializer.Deserialize(lines[i], SmartUpdaterJsonContext.Default.UpdateReport);
            }
            catch (JsonException ex)
            {
                _log.Warning(null, $"reports.jsonl 第 {lineNumber} 行损坏，已丢弃", ex);
                continue;
            }

            if (report is null)
            {
                _log.Warning(null, $"reports.jsonl 第 {lineNumber} 行损坏，已丢弃");
                continue;
            }

            list.Add(report);
        }

        DropExpired(list);
        return list;
    }

    private void DropExpired(List<UpdateReport> list)
    {
        DateTimeOffset now = _time.GetUtcNow();
        int dropped = list.RemoveAll(r => now - r.ReportedAt > MaxAge);
        if (dropped > 0)
        {
            _log.Debug(null, $"上报队列丢弃 {dropped} 条超过 {MaxAge.TotalDays:0} 天的过期条目");
        }
    }

    /// <summary>调用方必须持锁。空队列删除文件；否则整体原子重写。</summary>
    private void Save(List<UpdateReport> list)
    {
        if (list.Count == 0)
        {
            _fs.Delete(_filePath);
            return;
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !_fs.DirectoryExists(directory))
        {
            _fs.CreateDirectory(directory);
        }

        string content = string.Join("\n", list.Select(Serialize)) + "\n";
        AtomicFile.Write(_fs, _filePath, Utf8NoBom.GetBytes(content));
    }

    private static string Serialize(UpdateReport report)
        => JsonSerializer.Serialize(report, SmartUpdaterJsonContext.Default.UpdateReport);
}
