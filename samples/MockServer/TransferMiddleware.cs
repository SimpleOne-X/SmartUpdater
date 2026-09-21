namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 限速与中途断流。两者共用一个开关，因为断点续传的场景要配合使用。
/// 匹配规则与故障一致：精确路径、大小写不敏感、永远跳过 /_control/。
/// 必须排在 UseStaticFiles 之前：这里把响应体换成 <see cref="ThrottleStream"/>，静态文件才会写进它。
/// </summary>
internal static class TransferMiddleware
{
    public static void Use(WebApplication app, ControlState state)
    {
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? "/";

            if (path.StartsWith(FaultMiddleware.ControlPrefix, StringComparison.OrdinalIgnoreCase)
                || !state.TryGetTransfer(path, out int bytesPerSecond, out long? cutAfterBytes))
            {
                await next(context);
                return;
            }

            Stream original = context.Response.Body;
            var throttled = new ThrottleStream(original, bytesPerSecond, cutAfterBytes);
            context.Response.Body = throttled;

            try
            {
                // ThrottleStream 每写一块就 Flush，所以 next 返回之后没有任何字节还留在它里面，不需要再补一次 Flush。
                await next(context);
            }
            finally
            {
                context.Response.Body = original;
            }

            // 响应体被截短了。已经放行的字节都 Flush 过，客户端据此用 Range 续传，所以"到手的字节数恰好等于
            // cutAfterBytes"必须是确定的，而 Abort 保证不了：它会丢掉还留在 Kestrel 管道里没发出去的字节
            // （例如 200000 字节的文件 cutAfterBytes=40960，Abort 之后客户端只收到 4096）。
            //
            // 响应带 Content-Length 时（静态文件恒如此）不用 Abort：Kestrel 发现"写的比声明的少"会在送完全部已写字节之后
            // 关闭连接，客户端读到一半得到 IOException（HttpIOException：响应提前结束），一个字节都不会丢。
            // 没有 Content-Length（分块传输）时 Kestrel 会把截短的响应当成正常结束，客户端看不到任何错误，
            // 这时只能 Abort —— 宁可让到手的字节数不确定，也不能让截断悄无声息。
            if (throttled.WasCut && context.Response.ContentLength is null)
            {
                context.Abort();
            }
        });
    }
}
