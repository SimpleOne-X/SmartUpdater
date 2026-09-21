using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>三个运行参数的上限。</summary>
public class ClientPolicyCeilingTests
{
    private static readonly ResolvedClientSettings Local = new(TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(3), TimeSpan.FromHours(2));

    [Fact]
    public void Ceilings_are_one_day_one_day_seven_days()
    {
        Assert.Equal(TimeSpan.FromDays(1), ClientPolicyLimits.MaxPollInterval);
        Assert.Equal(TimeSpan.FromDays(1), ClientPolicyLimits.MaxJitterWindow);
        Assert.Equal(TimeSpan.FromDays(7), ClientPolicyLimits.MaxHeartbeatInterval);
    }

    [Fact]
    public void Feed_values_above_the_ceiling_are_clamped()
    {
        var feed = new ClientPolicy { PollIntervalSeconds = int.MaxValue, JitterWindowSeconds = 200_000, HeartbeatIntervalSeconds = 30 * 86400 };

        ResolvedClientSettings settings = ClientPolicyResolver.Resolve(feed, Local);

        Assert.Equal(TimeSpan.FromDays(1), settings.PollInterval);
        Assert.Equal(TimeSpan.FromDays(1), settings.JitterWindow);
        Assert.Equal(TimeSpan.FromDays(7), settings.HeartbeatInterval);
    }

    [Fact]
    public void Local_values_above_the_ceiling_are_clamped_too()
    {
        var local = new ResolvedClientSettings(TimeSpan.FromDays(3), TimeSpan.FromDays(2), TimeSpan.FromDays(30));

        ResolvedClientSettings settings = ClientPolicyResolver.Resolve(null, local);

        Assert.Equal(TimeSpan.FromDays(1), settings.PollInterval);
        Assert.Equal(TimeSpan.FromDays(1), settings.JitterWindow);
        Assert.Equal(TimeSpan.FromDays(7), settings.HeartbeatInterval);
    }

    [Fact]
    public void Values_at_the_ceiling_pass_unchanged_and_floors_still_apply()
    {
        var feed = new ClientPolicy { PollIntervalSeconds = 86400, JitterWindowSeconds = 86400, HeartbeatIntervalSeconds = 1 };

        ResolvedClientSettings settings = ClientPolicyResolver.Resolve(feed, Local);

        Assert.Equal(TimeSpan.FromDays(1), settings.PollInterval);
        Assert.Equal(TimeSpan.FromDays(1), settings.JitterWindow);
        Assert.Equal(ClientPolicyLimits.MinHeartbeatInterval, settings.HeartbeatInterval);
    }

    [Fact]
    public void Clamped_values_fit_Task_Delay()
    {
        ResolvedClientSettings settings = ClientPolicyResolver.Resolve(new ClientPolicy { PollIntervalSeconds = int.MaxValue }, Local);

        Task delay = Task.Delay(settings.PollInterval, new ManualTimeProvider(), TestContext.Current.CancellationToken);   // 超过 ~49.7 天会抛 ArgumentOutOfRangeException

        Assert.False(delay.IsCompleted);
    }
}
