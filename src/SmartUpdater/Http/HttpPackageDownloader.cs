using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 通过 HTTP(S) 下载升级包：写到 <c>.part</c> 临时文件，支持 Range / If-Range 断点续传，
/// 对 429 / 503 与传输中断做指数退避重试（尊重 Retry-After），完成后改名为目标文件。
/// 本类不拥有传入的 <see cref="HttpClient"/>，不会 <c>Dispose</c> 它。
/// </summary>
public sealed class HttpPackageDownloader : IPackageDownloader
{
    internal const int MaxRetries = 5;
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    internal const string PartSuffix = ".part";
    internal const string ETagSuffix = ".part.etag";
    private const int BufferSize = 64 * 1024;

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;

    /// <summary>创建下载器。</summary>
    /// <param name="httpClient">用于发请求的客户端；本类不拥有它，也不会释放它。</param>
    /// <param name="baseUri">解析相对 <c>package.url</c> 的基准（通常是 feed 的地址）；为 null 时只接受绝对 URL。</param>
    public HttpPackageDownloader(HttpClient httpClient, Uri? baseUri = null)
        : this(httpClient, baseUri, TimeProvider.System)
    {
    }

    internal HttpPackageDownloader(HttpClient httpClient, Uri? baseUri, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _http = httpClient;
        _timeProvider = timeProvider;
        BaseUri = baseUri;
        DelayAsync = (delay, ct) => Task.Delay(delay, timeProvider, ct);
    }

    /// <summary>解析相对 <c>package.url</c> 的基准地址；为 null 表示只接受绝对 URL。</summary>
    public Uri? BaseUri { get; }

    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; }

    /// <summary>把 <paramref name="release"/> 的包下载到 <paramref name="destinationPath"/>，返回最终文件路径。</summary>
    /// <param name="release">要下载的版本条目。</param>
    /// <param name="destinationPath">目标文件路径；已存在且长度等于声明大小时直接返回。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="ct">取消令牌；取消时保留 <c>.part</c> 以便下次续传。</param>
    /// <returns>最终文件路径。</returns>
    public async Task<string> DownloadAsync(ReleaseEntry release, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        Uri uri = ResolvePackageUri(BaseUri, release.Package.Url);
        long size = release.Package.Size;
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == size)
        {
            return destinationPath;
        }

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string part = destinationPath + PartSuffix;
        string etagFile = destinationPath + ETagSuffix;
        int retries = 0;
        bool restart = false;   // 上一次要求从零重下：下一次尝试直接截断 .part，不依赖先删除（删除可能被杀毒软件短暂占用）

        while (true)
        {
            Attempt outcome;
            try
            {
                outcome = await AttemptAsync(uri, size, destinationPath, part, etagFile, restart, progress, ct).ConfigureAwait(false);
            }
            catch (DiskWriteException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;   // 不可达：Throw() 总会抛出
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                outcome = Attempt.Transient(ex);
            }
            catch (IOException ex)
            {
                outcome = Attempt.Transient(ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                outcome = Attempt.Transient(ex);   // 读超时或 HttpClient.Timeout，不是调用方取消
            }

            restart = outcome.Immediate;

            if (outcome.Completed)
            {
                return destinationPath;
            }

            if (retries >= MaxRetries)
            {
                ExceptionDispatchInfo.Capture(outcome.Failure!).Throw();
            }

            TimeSpan delay = ComputeBackoff(retries, outcome.RetryAfter);
            retries++;
            if (!outcome.Immediate)
            {
                await DelayAsync(delay, ct).ConfigureAwait(false);
            }
        }
    }

    internal static Uri ResolvePackageUri(Uri? baseUri, string packageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageUrl);
        if (Uri.TryCreate(packageUrl, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute;
        }

        if (baseUri is null)
        {
            throw new ArgumentException($"包地址 \"{packageUrl}\" 是相对路径，但没有提供基准地址。", nameof(packageUrl));
        }

        return new Uri(baseUri, packageUrl);
    }

    internal static TimeSpan ComputeBackoff(int retryIndex, TimeSpan? retryAfter)
    {
        if (retryAfter is { } explicitDelay)
        {
            return explicitDelay < TimeSpan.Zero ? TimeSpan.Zero : Cap(explicitDelay);
        }

        // 30 s × 2^index；index ≥ 5 时已必然超过上限，直接封顶，避免移位溢出。
        int index = Math.Max(retryIndex, 0);
        return index >= 5 ? MaxBackoff : Cap(InitialBackoff * (1 << index));
    }

    private static TimeSpan Cap(TimeSpan value) => value > MaxBackoff ? MaxBackoff : value;

    private async Task<Attempt> AttemptAsync(Uri uri, long size, string destinationPath, string part, string etagFile, bool restart, IProgress<DownloadProgress>? progressSink, CancellationToken ct)
    {
        long offset = !restart && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset >= size)
        {
            DeleteQuietly(etagFile);
            offset = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (File.Exists(etagFile))
            {
                request.Headers.TryAddWithoutValidation("If-Range", File.ReadAllText(etagFile).Trim());
            }
        }

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        HttpStatusCode status = response.StatusCode;

        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return Attempt.Retry(new HttpRequestException($"下载 {uri} 返回 {(int)status}", null, status), RetryAfterOf(response));
        }

        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 在本类的调用序列下基本不可达（过长的 .part 已在上面丢弃），保留只作防御：下一次尝试截断 .part 从零重下。
            DeleteQuietly(etagFile);
            return Attempt.RetryNow(new HttpRequestException($"下载 {uri} 返回 416", null, status));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载 {uri} 返回 {(int)status}", null, status);
        }

        bool append = false;
        if (status == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range is { HasRange: true } && range.From == offset && (range.Length is null || range.Length == size))
            {
                append = true;
            }
            else
            {
                DeleteQuietly(etagFile);
                return Attempt.RetryNow(new HttpRequestException($"下载 {uri} 的 206 响应与请求的续传位置 {offset} 不符", null, status));
            }
        }
        else if (offset > 0)
        {
            offset = 0;   // 200：服务器不支持续传或内容已变，从零开始
        }

        FileStream file;
        try
        {
            if (response.Headers.ETag is { } etag)
            {
                File.WriteAllText(etagFile, etag.ToString());
            }
            else
            {
                DeleteQuietly(etagFile);
            }

            file = await OpenPartForWriteAsync(part, append, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(part);
            DeleteQuietly(etagFile);
            throw new DiskWriteException(ex);
        }

        var progress = new TransferProgress(size, offset, _timeProvider, progressSink);
        using (file)
        {
            Stream network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (network.ConfigureAwait(false))
            {
                using var readCts = new CancellationTokenSource(ReadTimeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readCts.Token);
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    readCts.CancelAfter(ReadTimeout);
                    int n = await network.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        await file.DisposeAsync().ConfigureAwait(false);
                        DeleteQuietly(part);
                        DeleteQuietly(etagFile);
                        throw new DiskWriteException(ex);
                    }

                    progress.Add(n);
                }
            }
        }

        long length = new FileInfo(part).Length;
        if (length != size)
        {
            return Attempt.Retry(new IOException($"下载 {uri} 提前结束，只收到 {length}/{size} 字节"), null);
        }

        try
        {
            File.Move(part, destinationPath, overwrite: true);
            File.Delete(etagFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(part);
            DeleteQuietly(etagFile);
            throw new DiskWriteException(ex);
        }

        progress.Complete();
        return Attempt.Done;
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            TimeSpan remaining = date - _timeProvider.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    /// <summary>
    /// 打开写入用的 <c>.part</c>。刚写下的文件可能被杀毒软件或索引服务短暂占用（ERROR_SHARING_VIOLATION），
    /// 这种冲突几十毫秒内就会消失，所以有限次重试；其他 IO 错误立即抛出。
    /// </summary>
    internal static async Task<FileStream> OpenPartForWriteAsync(string part, bool append, CancellationToken ct)
    {
        const int SharingViolation = 32;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xFFFF) == SharingViolation)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>磁盘写失败的内部标记：不重试，由外层拆开后原样抛出内部异常。</summary>
    private sealed class DiskWriteException(Exception inner) : Exception("磁盘写入失败", inner);

    /// <summary>单次尝试的结果：完成，或需要重试（带失败原因、服务器给的 Retry-After、是否免等待）。</summary>
    private readonly struct Attempt
    {
        private Attempt(bool completed, Exception? failure, TimeSpan? retryAfter, bool immediate)
        {
            Completed = completed;
            Failure = failure;
            RetryAfter = retryAfter;
            Immediate = immediate;
        }

        public static Attempt Done => new(true, null, null, false);

        public bool Completed { get; }

        public Exception? Failure { get; }

        public TimeSpan? RetryAfter { get; }

        public bool Immediate { get; }

        /// <summary>429 / 503 / 提前结束：按 Retry-After（有则用）或退避表等待后重试。</summary>
        public static Attempt Retry(Exception failure, TimeSpan? retryAfter) => new(false, failure, retryAfter, false);

        /// <summary>传输错误：按退避表等待后续传。</summary>
        public static Attempt Transient(Exception failure) => new(false, failure, null, false);

        /// <summary>416 与不符的 206：不等待，直接重下（仍计一次重试）。</summary>
        public static Attempt RetryNow(Exception failure) => new(false, failure, null, true);
    }
}
