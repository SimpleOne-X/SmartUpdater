using System.Net.Http.Headers;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 通过 HTTP <c>POST</c> 把上报记录以 JSON 送到服务端。本类不拥有传入的 <see cref="HttpClient"/>，不会 <c>Dispose</c> 它。
/// 2xx 视为送达；其他状态与传输异常返回 false，让记录留在队列里下次再试；调用方取消原样传播。
/// </summary>
public sealed class HttpUpdateReporter : IUpdateReporter
{
    private readonly HttpClient _http;

    /// <summary>用绝对 http/https 地址字符串创建上报器。</summary>
    /// <param name="httpClient">发送请求用的客户端；本类不拥有它，不会释放它。</param>
    /// <param name="reportUrl">上报地址，必须是绝对的 http 或 https URL。</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> 或 <paramref name="reportUrl"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="reportUrl"/> 不是绝对的 http/https URL。</exception>
    public HttpUpdateReporter(HttpClient httpClient, string reportUrl)
        : this(httpClient, ParseUrl(reportUrl))
    {
    }

    /// <summary>用绝对 http/https 地址创建上报器。</summary>
    /// <param name="httpClient">发送请求用的客户端；本类不拥有它，不会释放它。</param>
    /// <param name="reportUri">上报地址，必须是绝对的 http 或 https URI。</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> 或 <paramref name="reportUri"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="reportUri"/> 不是绝对的 http/https URI。</exception>
    public HttpUpdateReporter(HttpClient httpClient, Uri reportUri)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(reportUri);
        if (!IsHttp(reportUri))
        {
            throw new ArgumentException("上报地址必须是绝对的 http 或 https URL。", nameof(reportUri));
        }

        _http = httpClient;
        ReportUri = reportUri;
    }

    /// <summary>上报请求发往的地址。</summary>
    public Uri ReportUri { get; }

    /// <summary>发送一条上报记录。</summary>
    /// <param name="report">要发送的记录。</param>
    /// <param name="ct">取消令牌；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>服务端返回 2xx 时为 true；其他状态或传输失败为 false。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> 为 null。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 已取消。</exception>
    public async Task<bool> SendAsync(UpdateReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();

        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(report, SmartUpdaterJsonContext.Default.UpdateReport))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ReportUri) { Content = content };

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static Uri ParseUrl(string reportUrl)
    {
        ArgumentNullException.ThrowIfNull(reportUrl);
        if (!Uri.TryCreate(reportUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("上报地址必须是绝对的 http 或 https URL。", nameof(reportUrl));
        }

        return uri;
    }

    private static bool IsHttp(Uri uri)
        => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
