using System.Net;
using System.Net.Http.Headers;

namespace SmartUpdater.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? IfNoneMatch, string? Range, string? IfRange, string? Body);

/// <summary>脚本化的 HttpMessageHandler：按顺序弹出预置响应，并把请求的关键字段记录下来（HttpClient 会释放请求内容，所以记录副本）。</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>脚本用尽后的兜底；为 null 时抛 InvalidOperationException（测试脚本写少了）。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _script.Enqueue((request, _) => Task.FromResult(responder(request)));
        return this;
    }

    public StubHttpMessageHandler Enqueue(HttpStatusCode status, string? body = null, string? etag = null, string mediaType = "application/json")
    {
        return Enqueue(_ => Build(status, body, etag, mediaType));
    }

    public StubHttpMessageHandler EnqueueException(Exception exception)
    {
        _script.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    public static HttpResponseMessage Build(HttpStatusCode status, string? body = null, string? etag = null, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType);
        }

        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? inm) ? string.Join(",", inm) : null,
            request.Headers.Range?.ToString(),
            request.Headers.IfRange?.ToString(),
            body));

        cancellationToken.ThrowIfCancellationRequested();

        if (_script.Count == 0)
        {
            if (Fallback is null)
            {
                throw new InvalidOperationException($"没有为 {request.Method} {request.RequestUri} 准备响应。");
            }

            return Fallback(request);
        }

        return await _script.Dequeue()(request, cancellationToken);
    }
}
