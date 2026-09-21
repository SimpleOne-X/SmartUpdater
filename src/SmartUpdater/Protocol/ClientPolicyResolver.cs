namespace SimpleOneX.SmartUpdater;

/// <summary>合并本地配置与 feed 下发后的最终运行参数。</summary>
/// <param name="PollInterval">轮询间隔。</param>
/// <param name="JitterWindow">抖动窗口。</param>
/// <param name="HeartbeatInterval">心跳上报间隔。</param>
internal readonly record struct ResolvedClientSettings(
    TimeSpan PollInterval,
    TimeSpan JitterWindow,
    TimeSpan HeartbeatInterval);

/// <summary>运行参数的硬下限与缺省值。服务端无法突破下限。</summary>
internal static class ClientPolicyLimits
{
    /// <summary>轮询间隔下限。</summary>
    public static TimeSpan MinPollInterval { get; } = TimeSpan.FromSeconds(60);

    /// <summary>抖动窗口下限。0 表示允许关闭抖动。</summary>
    public static TimeSpan MinJitterWindow { get; } = TimeSpan.Zero;

    /// <summary>心跳间隔下限。</summary>
    public static TimeSpan MinHeartbeatInterval { get; } = TimeSpan.FromSeconds(300);

    /// <summary>轮询间隔上限（下限之外也需要上限：Task.Delay 不接受过大的值）。</summary>
    public static TimeSpan MaxPollInterval { get; } = TimeSpan.FromDays(1);

    /// <summary>抖动窗口上限。</summary>
    public static TimeSpan MaxJitterWindow { get; } = TimeSpan.FromDays(1);

    /// <summary>心跳间隔上限。</summary>
    public static TimeSpan MaxHeartbeatInterval { get; } = TimeSpan.FromDays(7);

    /// <summary>三项参数的缺省值。</summary>
    public static ResolvedClientSettings Defaults { get; } = new(
        PollInterval: TimeSpan.FromSeconds(300),
        JitterWindow: TimeSpan.FromSeconds(600),
        HeartbeatInterval: TimeSpan.FromSeconds(21600));
}

/// <summary>两层配置的生效规则：feed 下发的值优先，未下发的沿用本地值，最后统一钳制下限与上限。</summary>
internal static class ClientPolicyResolver
{
    /// <summary>合并并钳制运行参数。</summary>
    /// <param name="fromFeed">feed 的 client 段；尚未拿到 feed，或 feed 不含 client 段时为 null。</param>
    /// <param name="local">本地配置值。</param>
    public static ResolvedClientSettings Resolve(ClientPolicy? fromFeed, ResolvedClientSettings local)
    {
        TimeSpan poll = Pick(fromFeed?.PollIntervalSeconds, local.PollInterval);
        TimeSpan jitter = Pick(fromFeed?.JitterWindowSeconds, local.JitterWindow);
        TimeSpan heartbeat = Pick(fromFeed?.HeartbeatIntervalSeconds, local.HeartbeatInterval);

        // 本地值同样要钳制：使用者写一个 1 秒，后果和服务端手滑一样严重。
        return new ResolvedClientSettings(
            PollInterval: Clamp(poll, ClientPolicyLimits.MinPollInterval, ClientPolicyLimits.MaxPollInterval),
            JitterWindow: Clamp(jitter, ClientPolicyLimits.MinJitterWindow, ClientPolicyLimits.MaxJitterWindow),
            HeartbeatInterval: Clamp(heartbeat, ClientPolicyLimits.MinHeartbeatInterval, ClientPolicyLimits.MaxHeartbeatInterval));
    }

    private static TimeSpan Pick(int? fromFeedSeconds, TimeSpan localValue)
        => fromFeedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : localValue;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan floor, TimeSpan ceiling)
        => value < floor ? floor : value > ceiling ? ceiling : value;
}
