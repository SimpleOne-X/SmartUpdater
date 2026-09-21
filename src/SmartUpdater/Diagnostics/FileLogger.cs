using System.Globalization;
using System.Security.AccessControl;
using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 按本地日期滚动的文件日志。一行一条纯文本，每条 Flush；主目录不可用时回退到备用目录，
/// 两处都不可用则禁用。构造与 <see cref="Write"/> 永不抛出：日志失败不能让更新失败。
/// </summary>
internal sealed class FileLogger : IUpdateLog, IDisposable
{
    /// <summary>单个日志文件的默认上限（5 MB）。</summary>
    public const long DefaultMaxFileBytes = 5 * 1024 * 1024;

    /// <summary>日志保留天数：今天与之前 6 天保留。</summary>
    public const int RetentionDays = 7;

    /// <summary>日志文件名前缀。</summary>
    public const string FilePrefix = "updater-";

    /// <summary>日志文件扩展名。</summary>
    public const string FileExtension = ".log";

    private const string DateFormat = "yyyyMMdd";
    private const string ContinuationIndent = "    ";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string? _fallbackDirectory;
    private readonly UpdateLogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private readonly long _maxFileBytes;

    private StreamWriter? _writer;
    private string? _activeDirectory;
    private string? _currentFilePath;
    private DateOnly _currentDate;
    private long _length;
    private bool _capReached;
    private bool _onFallback;

    /// <summary>创建日志器。主目录不可用则试回退目录，都不可用则禁用（<see cref="ActiveDirectory"/> 为 null）。</summary>
    /// <param name="primaryDirectory">首选日志目录。</param>
    /// <param name="fallbackDirectory">回退日志目录；没有则为 null。</param>
    /// <param name="minimumLevel">低于此级别的条目直接丢弃。</param>
    /// <param name="timeProvider">时钟；文件名与滚动按其本地日期。</param>
    /// <param name="maxFileBytes">单个文件的字节上限。</param>
    public FileLogger(string primaryDirectory, string? fallbackDirectory, UpdateLogLevel minimumLevel, TimeProvider timeProvider, long maxFileBytes = DefaultMaxFileBytes)
    {
        _fallbackDirectory = fallbackDirectory;
        _minimumLevel = minimumLevel;
        _time = timeProvider;
        _maxFileBytes = maxFileBytes;

        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

        // 构造期没有任何可记日志的地方：主目录为什么不可用，调用方从 ActiveDirectory != primaryDirectory 即可得知。
        if (!TryOpen(primaryDirectory, today, out _)
            && fallbackDirectory is not null
            && TryOpen(fallbackDirectory, today, out _))
        {
            _onFallback = true;
        }

        if (_writer is not null)
        {
            CleanupExpired(today);
        }
    }

    /// <summary>实际写入的目录；两个目录都不可用（日志已禁用）时为 null。</summary>
    public string? ActiveDirectory
    {
        get
        {
            lock (_gate)
            {
                return _activeDirectory;
            }
        }
    }

    /// <summary>当前（当天）日志文件的完整路径；禁用时为 null。</summary>
    public string? CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return _currentFilePath;
            }
        }
    }

    /// <summary>某个本地日期对应的文件名，例如 <c>updater-20260918.log</c>。</summary>
    public static string FileNameFor(DateOnly localDate)
        => FilePrefix + localDate.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>把一条日志格式化成单行：<c>UTC | 本地 | 级别 | 阶段 | 消息</c>。消息里的换行替换为空格。</summary>
    public static string FormatLine(DateTimeOffset utcNow, TimeZoneInfo zone, UpdateLogLevel level, UpdateStage? stage, string message)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utcNow, zone);
        string flat = message.ReplaceLineEndings(" ");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{utcNow.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} | {local:yyyy-MM-dd HH:mm:ss.fff zzz} | {LevelName(level)} | {stage?.ToString() ?? "-"} | {flat}");
    }

    /// <inheritdoc />
    public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            WriteLocked(level, stage, message, exception);
        }
    }

    /// <summary>关闭文件。之后的 <see cref="Write"/> 静默返回。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            // 每条都已 Flush，这里没有待写数据；即使关闭失败也没有别处可记，也没有调用方能处理。
            _ = CloseWriter();
        }
    }

    private static string LevelName(UpdateLogLevel level) => level switch
    {
        UpdateLogLevel.Debug => "DEBUG",
        UpdateLogLevel.Information => "INFO",
        UpdateLogLevel.Warning => "WARN",
        _ => "ERROR",
    };

    private static bool IsFileSystemFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static bool TryParseFileDate(string fileName, out DateOnly date)
    {
        date = default;
        return fileName.Length == FilePrefix.Length + DateFormat.Length + FileExtension.Length
            && fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(fileName.AsSpan(FilePrefix.Length, DateFormat.Length), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private void WriteLocked(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception)
    {
        if (_writer is null)
        {
            return;
        }

        DateTimeOffset local = _time.GetLocalNow();
        DateOnly today = DateOnly.FromDateTime(local.DateTime);
        if (today != _currentDate)
        {
            RollTo(today);
            if (_writer is null)
            {
                return;
            }
        }

        DateTimeOffset utcNow = local.ToUniversalTime();
        if (_length >= _maxFileBytes)
        {
            if (!_capReached)
            {
                _capReached = true;
                string sentinel = FormatLine(
                    utcNow,
                    _time.LocalTimeZone,
                    UpdateLogLevel.Warning,
                    null,
                    string.Create(CultureInfo.InvariantCulture, $"日志文件已达 {_maxFileBytes} 字节上限，今日剩余记录丢弃")) + Environment.NewLine;
                Append(sentinel, today);
            }

            return;
        }

        Append(BuildEntry(utcNow, level, stage, message, exception), today);
    }

    private string BuildEntry(DateTimeOffset utcNow, UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception)
    {
        string line = FormatLine(utcNow, _time.LocalTimeZone, level, stage, message);
        if (exception is null)
        {
            return line + Environment.NewLine;
        }

        // 一行一条与"完整 ToString()"两个要求的折中：异常放在条目后面，每行缩进 4 个空格；
        // 以时间戳开头的行永远是条目起点，缩进行是续行。
        var builder = new StringBuilder(line);
        foreach (string part in DescribeException(exception).ReplaceLineEndings("\n").Split('\n'))
        {
            builder.Append(Environment.NewLine).Append(ContinuationIndent).Append(part);
        }

        return builder.Append(Environment.NewLine).ToString();
    }

    /// <summary>异常的完整 <c>ToString()</c>。异常类自己的 <c>ToString()</c> 抛出时退化为类型全名，日志不能因为被记录的对象有 bug 而失败。</summary>
    private static string DescribeException(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (Exception ex)
        {
            Type type = exception.GetType();
            return $"{type.FullName ?? type.Name} (ToString 失败: {ex.GetType().FullName})";
        }
    }

    /// <summary>
    /// 追加一段文本。写失败：正在写主目录且有回退目录则切到回退目录重试一次，否则禁用。
    /// </summary>
    private void Append(string text, DateOnly today)
    {
        if (TryAppend(text, out Exception? failure))
        {
            return;
        }

        if (!TrySwitchToFallback(today, "写入日志文件失败", failure))
        {
            Disable();
            return;
        }

        // 切换后记的那条 Warning 自己也可能写失败（例如磁盘满且两个目录在同一个盘），
        // 那时 Disable() 已把 _writer 置空，没有东西可重试了。
        if (_writer is null)
        {
            return;
        }

        if (!TryAppend(text, out _))
        {
            Disable();
        }
    }

    private bool TryAppend(string text, out Exception? failure)
    {
        try
        {
            _writer!.Write(text);
            _length += Utf8NoBom.GetByteCount(text);
            failure = null;
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ex;
            return false;
        }
    }

    private void RollTo(DateOnly today)
    {
        Exception? closeFailure = CloseWriter();
        if (TryOpen(_activeDirectory!, today, out Exception? openFailure))
        {
            NoteFailure("关闭前一个日志文件失败", closeFailure);
            return;
        }

        if (!TrySwitchToFallback(today, "打开新一天的日志文件失败", openFailure))
        {
            Disable();
        }
    }

    /// <summary>已经在回退目录或没有回退目录返回 false。成功后在新文件里记一条 Warning 说明原因。</summary>
    private bool TrySwitchToFallback(DateOnly today, string reason, Exception? failure)
    {
        if (_onFallback || _fallbackDirectory is null)
        {
            return false;
        }

        // 关闭失败是写失败的连带现象（FileStream 缓冲区里还压着写不出去的数据），原因已经在 failure 里。
        _ = CloseWriter();
        if (!TryOpen(_fallbackDirectory, today, out _))
        {
            return false;
        }

        _onFallback = true;
        NoteFailure($"{reason}，已切换到回退目录 {_fallbackDirectory}", failure);
        return true;
    }

    /// <summary>有失败可记时，往当前日志文件写一条 Warning（受最低级别过滤）。</summary>
    private void NoteFailure(string message, Exception? failure)
    {
        if (failure is not null && UpdateLogLevel.Warning >= _minimumLevel)
        {
            WriteLocked(UpdateLogLevel.Warning, null, message, failure);
        }
    }

    private void Disable()
    {
        // 走到这里说明主、备目录都写不进去了，没有别处可记。
        _ = CloseWriter();
        _activeDirectory = null;
        _currentFilePath = null;
    }

    /// <summary>关闭当前 writer。失败时返回异常而不是抛出，由调用方决定往哪里记。</summary>
    private Exception? CloseWriter()
    {
        StreamWriter? writer = _writer;
        _writer = null;
        if (writer is null)
        {
            return null;
        }

        try
        {
            writer.Dispose();
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return ex;
        }
    }

    /// <summary>
    /// 以真正的 OS 级追加方式打开日志文件。.NET 的 <c>FileMode.Append</c> 只是"打开后把游标移到末尾"，
    /// 之后每个句柄按自己记的位置写，两个句柄（两个进程，例如更新交接时的新旧 exe）交替写会互相覆盖。
    /// 只申请 <c>AppendData</c>（没有 <c>WriteData</c>）的句柄由系统保证每次写都落在文件末尾。
    /// </summary>
    private static FileStream OpenForAppend(string path)
    {
        // 非 Windows 分支只为满足平台分析器：跨平台不是本包的目标，那里没有等价的 API 可用。
        return OperatingSystem.IsWindows()
            ? new FileInfo(path).Create(FileMode.Append, FileSystemRights.AppendData, FileShare.ReadWrite, 4096, FileOptions.None, null)
            : new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    private bool TryOpen(string directory, DateOnly date, out Exception? failure)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileNameFor(date));
            FileStream stream = OpenForAppend(path);
            try
            {
                long length = stream.Length;
                var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
                _writer = writer;
                _length = length;
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            _activeDirectory = directory;
            _currentFilePath = path;
            _currentDate = date;
            _capReached = false;
            failure = null;
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ex;
            return false;
        }
    }

    private void CleanupExpired(DateOnly today)
    {
        string directory = _activeDirectory!;
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, FilePrefix + "*" + FileExtension);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            Write(UpdateLogLevel.Warning, null, $"无法枚举过期日志: {directory}", ex);
            return;
        }

        foreach (string path in candidates)
        {
            string name = Path.GetFileName(path);
            if (!TryParseFileDate(name, out DateOnly date) || today.DayNumber - date.DayNumber < RetentionDays)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                Write(UpdateLogLevel.Warning, null, $"无法删除过期日志 {name}", ex);
            }
        }
    }
}
