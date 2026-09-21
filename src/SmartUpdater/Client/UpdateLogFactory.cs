namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 按选项组装日志：文件日志与消费方回调都可选，互不排斥。
/// 文件日志最低级别 Information，避免被调试细节淹没；回调收到全部级别（含 Debug）。
/// </summary>
internal static class UpdateLogFactory
{
    /// <summary>
    /// 两者都无 → <see cref="NullUpdateLog.Instance"/>；只有一个 → 它自己；两个 → <see cref="CompositeLog"/>（文件在前，回调在后）。
    /// 返回的 <see cref="FileLogger"/>（若有）由调用方（<see cref="UpdateEngine"/>）负责释放。
    /// </summary>
    public static (IUpdateLog Log, FileLogger? FileLogger) Create(
        UpdateLayout layout,
        bool enableFileLogging,
        Action<UpdateLogLevel, string, Exception?>? callback,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(timeProvider);

        FileLogger? fileLogger = enableFileLogging
            ? new FileLogger(layout.LogDirectory, layout.FallbackLogDirectory, UpdateLogLevel.Information, timeProvider)
            : null;
        CallbackLog? callbackLog = callback is null ? null : new CallbackLog(callback);

        IUpdateLog log = (fileLogger, callbackLog) switch
        {
            (null, null) => NullUpdateLog.Instance,
            ({ } file, null) => file,
            (null, { } cb) => cb,
            ({ } file, { } cb) => new CompositeLog(file, cb),
        };
        return (log, fileLogger);
    }
}
