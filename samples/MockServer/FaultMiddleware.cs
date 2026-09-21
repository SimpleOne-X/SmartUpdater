using System.Globalization;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 按精确路径短路请求，用来构造 404 / 429 / 503 等故障。
/// 永不拦截 /_control/ —— 否则一条打错的故障会把控制面自己锁死，测试再也无法恢复。
/// </summary>
internal static class FaultMiddleware
{
    public const string ControlPrefix = "/_control/";

    public static void Use(WebApplication app, ControlState state)
    {
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? "/";

            if (path.StartsWith(ControlPrefix, StringComparison.OrdinalIgnoreCase)
                || !state.TryTakeFault(path, out int status, out int? retryAfterSeconds))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = status;
            if (retryAfterSeconds is { } seconds)
            {
                context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }
        });
    }
}
