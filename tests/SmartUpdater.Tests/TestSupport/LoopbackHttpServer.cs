using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmartUpdater.Tests;

/// <summary>
/// 进程内 HttpListener。非管理员账户可在 http://127.0.0.1:&lt;端口&gt;/ 监听（实测，Medium 完整性级别）。
/// 用于需要真实 HTTP 语义（Range / 断流 / 429）的测试；纯协议逻辑用 StubHttpMessageHandler。
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, Task>> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _stallRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Task _loop;

    public LoopbackHttpServer()
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }

        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>加锁复制当前已记录的请求（accept 线程仍在写入时读取用）。</summary>
    public RecordedRequest[] SnapshotRequests()
    {
        lock (_gate)
        {
            return [.. Requests];
        }
    }

    public int RequestCount(string path)
    {
        lock (_gate)
        {
            return _hits.GetValueOrDefault(path);
        }
    }

    public void Map(string path, Func<HttpListenerRequest, HttpListenerResponse, Task> handler)
    {
        lock (_gate)
        {
            _routes[path] = handler;
        }
    }

    public void ServeJson(string path, string json, string? etag = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        Map(path, async (request, response) =>
        {
            if (etag is not null && request.Headers["If-None-Match"] == etag)
            {
                response.StatusCode = 304;
                response.Headers["ETag"] = etag;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            if (etag is not null) { response.Headers["ETag"] = etag; }
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            response.Close();
        });
    }

    public void ServeBytes(string path, byte[] body, string? etag = null, bool supportRange = true)
        => Map(path, (request, response) => WriteBytesAsync(request, response, body, etag, supportRange, abortAfter: null, stallAfter: null));

    /// <summary>前 failures 次回 status（可带 Retry-After），之后按 ServeBytes。</summary>
    public void FailThenServe(string path, int failures, HttpStatusCode status, byte[] body, string? retryAfter = null)
    {
        int remaining = failures;
        Map(path, (request, response) =>
        {
            if (Interlocked.Decrement(ref remaining) >= 0)
            {
                response.StatusCode = (int)status;
                if (retryAfter is not null) { response.Headers["Retry-After"] = retryAfter; }
                response.Close();
                return Task.CompletedTask;
            }

            return WriteBytesAsync(request, response, body, etag: null, supportRange: true, abortAfter: null, stallAfter: null);
        });
    }

    /// <summary>第一次请求写 bytes 字节后 Abort；之后按 ServeBytes（支持 Range）。</summary>
    public void AbortAfter(string path, byte[] body, int bytes, string? etag = null)
    {
        int first = 1;
        Map(path, (request, response) => WriteBytesAsync(request, response, body, etag, supportRange: true,
            abortAfter: Interlocked.Exchange(ref first, 0) == 1 ? bytes : null, stallAfter: null));
    }

    /// <summary>第一次请求写 bytes 字节后挂起直到 Dispose；之后按 ServeBytes（支持 Range）。</summary>
    public void StallAfter(string path, byte[] body, int bytes, string? etag = null)
    {
        int first = 1;
        Map(path, (request, response) => WriteBytesAsync(request, response, body, etag, supportRange: true,
            abortAfter: null, stallAfter: Interlocked.Exchange(ref first, 0) == 1 ? bytes : null));
    }

    private async Task WriteBytesAsync(HttpListenerRequest request, HttpListenerResponse response, byte[] body, string? etag, bool supportRange, int? abortAfter, int? stallAfter)
    {
        if (etag is not null) { response.Headers["ETag"] = etag; }
        response.Headers["Accept-Ranges"] = supportRange ? "bytes" : "none";

        int from = 0;
        string? range = request.Headers["Range"];
        string? ifRange = request.Headers["If-Range"];
        if (supportRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && (ifRange is null || ifRange == etag))
        {
            from = int.Parse(range["bytes=".Length..].TrimEnd('-'), System.Globalization.CultureInfo.InvariantCulture);
            if (from >= body.Length)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{body.Length}";
                response.Close();
                return;
            }

            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
        }
        else
        {
            response.StatusCode = 200;
        }

        response.ContentLength64 = body.Length - from;
        int limit = body.Length;
        if (abortAfter is { } a) { limit = Math.Min(limit, from + a); }
        if (stallAfter is { } s) { limit = Math.Min(limit, from + s); }

        await response.OutputStream.WriteAsync(body.AsMemory(from, limit - from));
        await response.OutputStream.FlushAsync();

        if (abortAfter is not null)
        {
            response.Abort();
            return;
        }

        if (stallAfter is not null)
        {
            await _stallRelease.Task;
            response.Abort();
            return;
        }

        response.Close();
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;   // Stop/Close 之后 GetContextAsync 抛出，正常退出
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string path = request.Url!.AbsolutePath;
        string? body = null;
        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            body = await reader.ReadToEndAsync();
        }

        Func<HttpListenerRequest, HttpListenerResponse, Task>? handler;
        lock (_gate)
        {
            Requests.Add(new RecordedRequest(new HttpMethod(request.HttpMethod), request.Url, request.Headers["If-None-Match"], request.Headers["Range"], request.Headers["If-Range"], body));
            _hits[path] = _hits.GetValueOrDefault(path) + 1;
            _routes.TryGetValue(path, out handler);
        }

        try
        {
            if (handler is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            await handler(request, context.Response);
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stallRelease.TrySetResult();
        try { _listener.Stop(); } catch (Exception) { }
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
