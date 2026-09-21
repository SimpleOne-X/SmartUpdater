namespace SmartUpdater.Tests;

public class ManualTimeProviderTests
{
    [Fact]
    public async Task Delay_completes_only_when_advanced_past_due()
    {
        var time = new ManualTimeProvider();
        Task delay = Task.Delay(TimeSpan.FromMinutes(5), time, TestContext.Current.CancellationToken);

        Assert.False(delay.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(delay.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));

        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task WaitAsync_times_out_when_advanced()
    {
        var time = new ManualTimeProvider();
        Task never = new TaskCompletionSource().Task.WaitAsync(TimeSpan.FromSeconds(15), time, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<TimeoutException>(() => never.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForTimersAsync_releases_when_the_code_under_test_starts_waiting()
    {
        var time = new ManualTimeProvider();
        Task waiter = time.WaitForTimersAsync(1);
        Assert.False(waiter.IsCompleted);

        Task delay = Task.Delay(TimeSpan.FromSeconds(1), time, TestContext.Current.CancellationToken);

        await waiter;
        time.Advance(TimeSpan.FromSeconds(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Timers_fire_in_due_order_and_now_matches_each_due_time()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fired = new List<(string Name, DateTimeOffset At)>();
        using ITimer b = time.CreateTimer(_ => fired.Add(("b", time.GetUtcNow())), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        using ITimer a = time.CreateTimer(_ => fired.Add(("a", time.GetUtcNow())), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(["a", "b"], fired.Select(f => f.Name));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 10, TimeSpan.Zero), fired[0].At);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 20, TimeSpan.Zero), fired[1].At);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero), time.GetUtcNow());
    }

    [Fact]
    public void Periodic_timer_rearms_and_timestamp_tracks_the_clock()
    {
        var time = new ManualTimeProvider();
        int count = 0;
        long start = time.GetTimestamp();
        using ITimer t = time.CreateTimer(_ => count++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(3.5));

        Assert.Equal(3, count);
        Assert.Equal(TimeSpan.FromSeconds(3.5), time.GetElapsedTime(start));
    }

    [Fact]
    public async Task CancellationTokenSource_with_provider_cancels_on_advance()
    {
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(cts.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1, cts.Token));
    }
}
