using System.Net;
using Microsoft.Extensions.FileProviders;
using static SimpleOneX.SmartUpdater.MockServer.Tests.TransferTestSupport;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// 静态文件之外的中间件行为，用一个只有 TransferMiddleware 和几个手写端点的迷你服务器来测：
/// 静态文件的响应恒带 Content-Length，Kestrel 会在"写的比声明的少"时自己关闭连接；
/// 没有 Content-Length 的响应（分块传输）就不会 —— 截短之后 Kestrel 照样发出结束块，客户端会把残缺的响应体当成完整的，
/// 这时候只有 Abort 能让客户端看到错误。另外守住"中间件用完之后把 Response.Body 还回去"。
/// </summary>
public sealed class TransferMiddlewareTests
{
    private const int BodySize = 100_000;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static async Task<(WebApplication App, Uri BaseAddress)> StartAsync(
        TransferRequest[] transfers, Action<WebApplication>? beforeTransfer, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.SuppressStatusMessages(true);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();

        var state = new ControlState(Path.GetTempPath(), new OverlayFileProvider(new NullFileProvider()));
        foreach (TransferRequest transfer in transfers)
        {
            state.SetTransfer(transfer);
        }

        beforeTransfer?.Invoke(app);
        TransferMiddleware.Use(app, state);

        byte[] body = MakePackage(BodySize);
        app.MapGet("/blob", async (HttpContext context) =>
        {
            // 不设 Content-Length：分块传输。分几次写并 Flush，让响应先开始再被截断。
            for (int offset = 0; offset < body.Length; offset += 10_000)
            {
                await context.Response.Body.WriteAsync(body.AsMemory(offset, 10_000), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });
        app.MapGet("/boom", (HttpContext _) => throw new InvalidOperationException("端点故意失败"));

        await app.StartAsync(cancellationToken);
        return (app, new Uri(app.Urls.Single().TrimEnd('/') + "/"));
    }

    private static async Task StopAsync(WebApplication app)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StopAsync(budget.Token);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task A_cut_chunked_response_is_aborted_so_the_client_sees_an_error()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (WebApplication app, Uri baseAddress) = await StartAsync([new TransferRequest("/blob", null, 30_000)], null, cancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            byte[] received = [];
            Exception? failure = null;
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    "blob", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                Assert.Null(response.Content.Headers.ContentLength);

                (received, failure) = await ReadBodyAsync(response, cancellationToken);
            }
            catch (HttpRequestException e)
            {
                // Abort 会丢掉还没发出去的字节，负载高的时候连响应头都可能没发出去，连接就被重置了：
                // 失败出在收响应头的阶段而不是读响应体的阶段，同样是"客户端看到了错误"。
                failure = e;
            }

            // 没有 Abort 的话客户端会读到 30000 字节然后"正常结束"，把残缺的响应当成完整的。
            Assert.NotNull(failure);
            Assert.True(received.Length <= 30_000, $"收到 {received.Length} 字节，超过了 cutAfterBytes");
            Assert.Equal(MakePackage(BodySize)[..received.Length], received);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task A_chunked_response_shorter_than_the_limit_completes_normally()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (WebApplication app, Uri baseAddress) = await StartAsync([new TransferRequest("/blob", null, BodySize)], null, cancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };

            byte[] received = await client.GetByteArrayAsync("blob", cancellationToken);

            Assert.Equal(MakePackage(BodySize), received);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task The_original_response_body_is_put_back_after_the_request()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var observed = new TaskCompletionSource<(Stream Before, Stream After)>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 排在 TransferMiddleware 之外的一层：看它进来时和出来时的 Response.Body 是不是同一个对象。
        (WebApplication app, Uri baseAddress) = await StartAsync(
            [new TransferRequest("/blob", 50_000_000, null)],
            app => app.Use(async (context, next) =>
            {
                Stream before = context.Response.Body;
                await next(context);
                observed.TrySetResult((before, context.Response.Body));
            }),
            cancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            byte[] received = await client.GetByteArrayAsync("blob", cancellationToken);
            Assert.Equal(MakePackage(BodySize), received);

            (Stream before, Stream after) = await observed.Task.WaitAsync(Deadline, cancellationToken);
            Assert.Same(before, after);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task The_original_response_body_is_put_back_even_when_the_endpoint_throws()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var observed = new TaskCompletionSource<(Stream Before, Stream After)>(TaskCreationOptions.RunContinuationsAsynchronously);

        (WebApplication app, Uri baseAddress) = await StartAsync(
            [new TransferRequest("/boom", 50_000_000, null)],
            app => app.Use(async (context, next) =>
            {
                Stream before = context.Response.Body;
                try
                {
                    await next(context);
                }
                catch (InvalidOperationException)
                {
                    observed.TrySetResult((before, context.Response.Body));
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }),
            cancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            using HttpResponseMessage response = await client.GetAsync("boom", cancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            (Stream before, Stream after) = await observed.Task.WaitAsync(Deadline, cancellationToken);
            Assert.Same(before, after);
        }
        finally
        {
            await StopAsync(app);
        }
    }
}
