using System.Net.Http;

namespace SimpleOneX.SmartUpdater;

/// <summary>一组传输实现。只有 <see cref="OwnedHttpClient"/>（本包自建的 HttpClient）归它释放；消费方自带的实现不归本包管。</summary>
/// <param name="Feed">feed 实现。</param>
/// <param name="Downloader">包下载器。</param>
/// <param name="Reporter">上报实现；不上报时为 null。</param>
/// <param name="OwnedHttpClient">本包自建的 HttpClient；没有创建时为 null。</param>
internal sealed record TransportSet(IReleaseFeed Feed, IPackageDownloader Downloader, IUpdateReporter? Reporter, HttpClient? OwnedHttpClient) : IDisposable
{
    /// <summary>只释放 <see cref="OwnedHttpClient"/>。</summary>
    public void Dispose() => OwnedHttpClient?.Dispose();
}

/// <summary>按选项装配传输实现。</summary>
internal static class TransportFactory
{
    /// <summary>自建 HttpClient 的连接池寿命，避免长期持有过期的 DNS 解析。</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>自建 HttpClient 的整体超时。</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// 装配传输：消费方给了什么就用什么；没给的按 FeedUrl / ReportUrl 用自建的 HttpClient 补齐；
    /// <c>FileReleaseFeed</c> 自动配 <see cref="FilePackageDownloader"/>。
    /// </summary>
    public static TransportSet Create(ResolvedClientOptions options, IUpdateLog log, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);

        UpdateClientOptions source = options.Source;
        HttpClient? http = null;
        HttpClient Http() => http ??= CreateHttpClient(source.AllowUntrustedCertificates);

        try
        {
            IReleaseFeed feed = source.Feed ?? new HttpReleaseFeed(Http(), options.FeedUri!);
            IPackageDownloader downloader = source.Downloader
                ?? (feed is FileReleaseFeed fileFeed
                    ? new FilePackageDownloader(fileFeed.FeedDirectory, timeProvider)
                    : new HttpPackageDownloader(Http(), (feed as HttpReleaseFeed)?.FeedUri, timeProvider));
            IUpdateReporter? reporter = source.Reporter
                ?? (options.ReportUri is null ? null : new HttpUpdateReporter(Http(), options.ReportUri));

            if (source.AllowUntrustedCertificates)
            {
                if (http is not null)
                {
                    log.Warning(null, "已放宽 TLS 证书校验（AllowUntrustedCertificates）：本包自建的 HttpClient 接受任何服务器证书，仅限内网自签名场景");
                }
                else
                {
                    log.Warning(null, "AllowUntrustedCertificates 对自带的 Feed / Downloader / Reporter 无效：本包没有创建 HttpClient，证书校验由自带实现负责");
                }
            }

            return new TransportSet(feed, downloader, reporter, http);
        }
        catch
        {
            http?.Dispose();
            throw;
        }
    }

    /// <summary>创建处理器：连接池寿命 5 分钟；<paramref name="allowUntrustedCertificates"/> 为 true 时不校验服务器证书。</summary>
    internal static SocketsHttpHandler CreateHandler(bool allowUntrustedCertificates)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = PooledConnectionLifetime };
        if (allowUntrustedCertificates)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    /// <summary>创建自建的 HttpClient（超时 100 秒）；处理器随 HttpClient 一起释放。</summary>
    internal static HttpClient CreateHttpClient(bool allowUntrustedCertificates)
        => new(CreateHandler(allowUntrustedCertificates)) { Timeout = HttpTimeout };
}
