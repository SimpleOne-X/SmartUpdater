using System.Net;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 通过 HTTP(S) 取回 feed。支持 ETag 条件请求（<c>If-None-Match</c> / 304）。
/// 本类不拥有传入的 <see cref="HttpClient"/>，不会 <c>Dispose</c> 它；其生命周期归调用方。
/// feed 层不记日志、不做语义校验：非 2xx 抛 <see cref="HttpRequestException"/>，畸形 JSON 抛 <see cref="System.Text.Json.JsonException"/>，
/// 连接失败与超时原样传播。
/// </summary>
public sealed class HttpReleaseFeed : IReleaseFeed
{
    private readonly HttpClient _httpClient;

    /// <summary>创建实例。</summary>
    /// <param name="httpClient">发送请求的客户端，本类不拥有它。</param>
    /// <param name="feedUrl">feed 的绝对 http/https 地址。</param>
    /// <exception cref="ArgumentException"><paramref name="feedUrl"/> 不是绝对的 http/https URL。</exception>
    public HttpReleaseFeed(HttpClient httpClient, string feedUrl)
        : this(httpClient, ParseUrl(feedUrl))
    {
    }

    /// <summary>创建实例。</summary>
    /// <param name="httpClient">发送请求的客户端，本类不拥有它。</param>
    /// <param name="feedUri">feed 的绝对 http/https 地址。</param>
    /// <exception cref="ArgumentException"><paramref name="feedUri"/> 不是绝对的 http/https URL。</exception>
    public HttpReleaseFeed(HttpClient httpClient, Uri feedUri)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(feedUri);
        if (!feedUri.IsAbsoluteUri || feedUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("feed 地址必须是绝对的 http/https URL", nameof(feedUri));
        }

        _httpClient = httpClient;
        FeedUri = feedUri;
    }

    /// <summary>feed 的地址。</summary>
    public Uri FeedUri { get; }

    /// <inheritdoc />
    public async Task<FeedResult> GetAsync(string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUri);
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return FeedResult.NotModified;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"获取 feed 失败：{FeedUri} 返回 {(int)response.StatusCode} {response.StatusCode}",
                null,
                response.StatusCode);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        ReleaseFeedDocument document = ReleaseFeedReader.Parse(bytes);
        return new FeedResult(false, document, response.Headers.ETag?.ToString());
    }

    private static Uri ParseUrl(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("feed 地址必须是绝对的 http/https URL", nameof(feedUrl));
        }

        return uri;
    }
}
