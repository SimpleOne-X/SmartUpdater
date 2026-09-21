namespace SimpleOneX.SmartUpdater;

/// <summary>日志级别。数值递增，便于按阈值过滤。</summary>
public enum UpdateLogLevel
{
    /// <summary>排障细节。</summary>
    Debug = 0,

    /// <summary>正常流程的决策与结果。</summary>
    Information = 1,

    /// <summary>可自愈的异常情况。</summary>
    Warning = 2,

    /// <summary>失败。</summary>
    Error = 3,
}
