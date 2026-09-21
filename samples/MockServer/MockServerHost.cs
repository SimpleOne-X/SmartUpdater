using System.Net;
using Microsoft.Extensions.FileProviders;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 进程内可启停的模拟服务器：只监听回环地址，端口由操作系统分配（<c>--port 0</c>）或由调用方指定。
/// 构造时不建站；<see cref="StartAsync"/> 里才装配管线并启动。
/// </summary>
internal sealed class MockServerHost : IAsyncDisposable
{
    /// <summary>
    /// <see cref="DisposeAsync"/> 里停服务器最多等多久，超时后 Kestrel 会把还没结束的连接中断。
    /// 空闲的服务器几毫秒就停完；这个上限只在有响应还在途中时起作用 —— 被限速 / 被切断 / 客户端不读的响应
    /// 不会自己结束，而 Kestrel 默认的宽限期是 <see cref="HostOptions.ShutdownTimeout"/> 的 30 秒，
    /// 每个测试类的 Dispose 都可能因此多等半分钟。5 秒足够让正常的在途请求（几百 KB 的静态文件，哪怕机器很忙）
    /// 自然收尾，又比 30 秒短得多。
    /// </summary>
    internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly MockServerOptions _options;
    private readonly TimeSpan _stopTimeout;
    private readonly PhysicalFileProvider _physicalFiles;
    private WebApplication? _app;
    private Uri? _baseAddress;

    /// <param name="options">启动参数。</param>
    /// <param name="stopTimeout">覆盖 <see cref="DefaultStopTimeout"/>；只有测试需要，用来让"超时后强制中断"的路径别拖慢整个测试运行。</param>
    public MockServerHost(MockServerOptions options, TimeSpan? stopTimeout = null)
    {
        _options = options;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), stopTimeout, "停止的超时必须为正数。");
        }

        // PhysicalFileProvider 只接受绝对路径，而 MockServerOptions.TryParse 只检查目录存在，所以相对的 --root 在这里规范化。
        _physicalFiles = new PhysicalFileProvider(Path.GetFullPath(options.Root));
        Files = new OverlayFileProvider(_physicalFiles);
        State = new ControlState(options.Root, Files);
    }

    /// <summary>静态托管用的文件提供者：包住 <c>--root</c> 的物理目录，并允许在内存里盖住个别路径（feed 叠加）。</summary>
    public OverlayFileProvider Files { get; }

    /// <summary>服务器的全部可变状态；控制面与各中间件共享同一份。</summary>
    public ControlState State { get; }

    /// <summary>服务器实际监听的地址，末尾带 <c>/</c>。<see cref="StartAsync"/> 之前读取会抛 <see cref="InvalidOperationException"/>。</summary>
    public Uri BaseAddress => _baseAddress
        ?? throw new InvalidOperationException("MockServerHost 尚未启动，请先调用 StartAsync。");

    /// <summary>端到端脚本 grep 的那一行：<c>LISTENING http://127.0.0.1:56557/</c>。</summary>
    public static string FormatListeningLine(Uri baseAddress) => $"LISTENING {baseAddress}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("MockServerHost 已经启动过。");
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // 框架日志与启动状态消息一律关闭：stdout 的第一行必须是 LISTENING。
        builder.Logging.ClearProviders();
        builder.WebHost.SuppressStatusMessages(true);

        // 只监听回环；端口 0 交给操作系统分配，不写死任何端口。
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _options.Port));

        WebApplication app = builder.Build();

        // 管线顺序：FaultMiddleware → TransferMiddleware → UseStaticFiles → 端点。
        // 理由：故障要能短路一切；限速要能包住静态文件的响应体；端点在 minimal hosting 下天然跑在最后，
        // 而 /_control/* 不是静态文件，会自然穿过 UseStaticFiles 落到路由。

        // 故障中间件排在最前：命中的请求在这里就被短路，静态文件根本不会被读（/_control/ 永远不被它拦截）。
        FaultMiddleware.Use(app, State);

        // 传输中间件紧随其后、UseStaticFiles 之前：它把响应体换成 ThrottleStream，静态文件才会写进被限速 / 被切断的流。
        TransferMiddleware.Use(app, State);

        // ETag、If-None-Match、Range、If-Range 全部由静态文件中间件原生处理，这里不写一行相关逻辑。
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = Files,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",

            // no-cache 让客户端每次都带 If-None-Match 回来，304 路径才会真的被走到。
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.Append("Cache-Control", "no-cache"),
        });

        // 端点排在 UseStaticFiles 之后（见上面的顺序）：上报端点（唯一的动态接口）与控制面。
        ControlEndpoints.Map(app, State);
        ReportEndpoint.Map(app, State);

        try
        {
            await app.StartAsync(cancellationToken);

            // Listen(..., 0) 之后 Urls 里只有一个元素，且端口已是操作系统分配的真实值。
            if (app.Urls.Count != 1)
            {
                throw new InvalidOperationException(
                    $"期望恰好一个监听地址，实际 {app.Urls.Count} 个: {string.Join(", ", app.Urls)}");
            }

            string url = app.Urls.Single();
            _baseAddress = new Uri(url.TrimEnd('/') + "/");
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        WebApplication app = _app ?? throw new InvalidOperationException("MockServerHost 尚未启动，请先调用 StartAsync。");
        return app.WaitForShutdownAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _app is null ? Task.CompletedTask : _app.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        WebApplication? app = _app;
        _app = null;
        try
        {
            if (app is not null)
            {
                try
                {
                    using var stopBudget = new CancellationTokenSource(_stopTimeout);
                    await app.StopAsync(stopBudget.Token);
                }
                finally
                {
                    await app.DisposeAsync();
                }
            }
        }
        finally
        {
            // 没启动过的实例也持有 PhysicalFileProvider，所以无论 _app 是否为空都要释放。
            _physicalFiles.Dispose();
        }
    }
}
