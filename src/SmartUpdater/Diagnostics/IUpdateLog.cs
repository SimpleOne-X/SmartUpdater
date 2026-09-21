namespace SimpleOneX.SmartUpdater;

/// <summary>包内所有组件记日志的唯一入口。实现必须不抛异常——日志失败不能影响更新。</summary>
internal interface IUpdateLog
{
    /// <summary>写一条日志。</summary>
    /// <param name="level">级别。</param>
    /// <param name="stage">所属阶段；与阶段无关时为 null。</param>
    /// <param name="message">单行消息。</param>
    /// <param name="exception">相关异常，实现应记录完整 <c>ToString()</c>。</param>
    void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null);
}

/// <summary>丢弃一切的日志实现。</summary>
internal sealed class NullUpdateLog : IUpdateLog
{
    /// <summary>唯一实例。</summary>
    public static NullUpdateLog Instance { get; } = new();

    private NullUpdateLog()
    {
    }

    public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
    {
    }
}

/// <summary>按级别命名的便捷重载。</summary>
internal static class UpdateLogExtensions
{
    public static void Debug(this IUpdateLog log, UpdateStage? stage, string message)
        => log.Write(UpdateLogLevel.Debug, stage, message);

    public static void Information(this IUpdateLog log, UpdateStage? stage, string message)
        => log.Write(UpdateLogLevel.Information, stage, message);

    public static void Warning(this IUpdateLog log, UpdateStage? stage, string message, Exception? exception = null)
        => log.Write(UpdateLogLevel.Warning, stage, message, exception);

    public static void Error(this IUpdateLog log, UpdateStage? stage, string message, Exception? exception = null)
        => log.Write(UpdateLogLevel.Error, stage, message, exception);
}
