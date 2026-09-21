using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ClientPolicyResolverTests
{
    private static readonly ResolvedClientSettings Local = new(
        PollInterval: TimeSpan.FromMinutes(7),
        JitterWindow: TimeSpan.FromMinutes(3),
        HeartbeatInterval: TimeSpan.FromHours(2));

    [Fact]
    public void Null_policy_keeps_local_settings()
    {
        var r = ClientPolicyResolver.Resolve(null, Local);

        Assert.Equal(Local, r);
    }

    [Fact]
    public void Feed_value_overrides_local()
    {
        var policy = new ClientPolicy { PollIntervalSeconds = 900 };

        var r = ClientPolicyResolver.Resolve(policy, Local);

        Assert.Equal(TimeSpan.FromSeconds(900), r.PollInterval);
        Assert.Equal(Local.JitterWindow, r.JitterWindow);        // 未下发的项保持本地值
        Assert.Equal(Local.HeartbeatInterval, r.HeartbeatInterval);
    }

    [Fact]
    public void Poll_interval_below_floor_is_clamped_to_60s()
    {
        var policy = new ClientPolicy { PollIntervalSeconds = 1 };

        var r = ClientPolicyResolver.Resolve(policy, Local);

        Assert.Equal(TimeSpan.FromSeconds(60), r.PollInterval);
    }

    [Fact]
    public void Heartbeat_below_floor_is_clamped_to_300s()
    {
        var policy = new ClientPolicy { HeartbeatIntervalSeconds = 10 };

        var r = ClientPolicyResolver.Resolve(policy, Local);

        Assert.Equal(TimeSpan.FromSeconds(300), r.HeartbeatInterval);
    }

    [Fact]
    public void Jitter_window_of_zero_is_allowed()
    {
        var policy = new ClientPolicy { JitterWindowSeconds = 0 };

        var r = ClientPolicyResolver.Resolve(policy, Local);

        Assert.Equal(TimeSpan.Zero, r.JitterWindow);
    }

    [Fact]
    public void Negative_values_are_clamped_to_floor()
    {
        var policy = new ClientPolicy
        {
            PollIntervalSeconds = -1,
            JitterWindowSeconds = -1,
            HeartbeatIntervalSeconds = -1,
        };

        var r = ClientPolicyResolver.Resolve(policy, Local);

        Assert.Equal(TimeSpan.FromSeconds(60), r.PollInterval);
        Assert.Equal(TimeSpan.Zero, r.JitterWindow);
        Assert.Equal(TimeSpan.FromSeconds(300), r.HeartbeatInterval);
    }

    [Fact]
    public void Local_settings_are_also_clamped()
    {
        var reckless = new ResolvedClientSettings(
            TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var r = ClientPolicyResolver.Resolve(null, reckless);

        Assert.Equal(TimeSpan.FromSeconds(60), r.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(300), r.HeartbeatInterval);
    }

    [Fact]
    public void Defaults_have_the_documented_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), ClientPolicyLimits.Defaults.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(600), ClientPolicyLimits.Defaults.JitterWindow);
        Assert.Equal(TimeSpan.FromSeconds(21600), ClientPolicyLimits.Defaults.HeartbeatInterval);
    }

    // 部分下发：feed 的 client 段存在、但缺某几项时，缺的那几项必须沿用本地值，而不是回落到 Defaults。
    // 三项各测一次；期望值全部取自 Local，它与 Defaults 逐项不同，所以任何回落到 Defaults 的实现都会被抓住。
    [Fact]
    public void Fields_missing_from_a_partial_feed_segment_keep_local_values()
    {
        var pollOnly = ClientPolicyResolver.Resolve(
            new ClientPolicy { PollIntervalSeconds = 900 }, Local);
        var jitterOnly = ClientPolicyResolver.Resolve(
            new ClientPolicy { JitterWindowSeconds = 120 }, Local);
        var heartbeatOnly = ClientPolicyResolver.Resolve(
            new ClientPolicy { HeartbeatIntervalSeconds = 900 }, Local);

        Assert.Equal(Local.JitterWindow, pollOnly.JitterWindow);
        Assert.Equal(Local.HeartbeatInterval, pollOnly.HeartbeatInterval);

        Assert.Equal(Local.PollInterval, jitterOnly.PollInterval);
        Assert.Equal(Local.HeartbeatInterval, jitterOnly.HeartbeatInterval);

        Assert.Equal(Local.PollInterval, heartbeatOnly.PollInterval);
        Assert.Equal(Local.JitterWindow, heartbeatOnly.JitterWindow);
    }
}
