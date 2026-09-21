namespace SimpleOneX.SmartUpdater.Samples.WinForms.Tests;

public sealed class WorkLoopTests
{
    [Fact]
    public async Task Task_is_available_right_after_Start_so_that_WaitFor_can_receive_it()
    {
        using var cts = new CancellationTokenSource();
        using var loop = new WorkLoop();
        loop.Start(cts.Token);

        // 工作循环的 Task 必须能被保存下来交给 Restarting 的 e.WaitFor(task)。
        Assert.NotNull(loop.Task);
        await loop.WaitForRoundAsync(TimeSpan.FromSeconds(10));   // 保证取消时循环正停在 Delay 里，取消才会以 OperationCanceledException 的形式抵达
        await cts.CancelAsync();
        await loop.Task;                                  // 取消后正常结束，不抛 OperationCanceledException
        Assert.True(loop.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancelling_finishes_the_loop_promptly_and_records_the_work_done()
    {
        using var cts = new CancellationTokenSource();
        using var loop = new WorkLoop();
        loop.Start(cts.Token);

        await loop.WaitForRoundAsync(TimeSpan.FromSeconds(10));   // 事件驱动，不是 Thread.Sleep
        int done = loop.CompletedRounds;
        Assert.True(done >= 1);

        await cts.CancelAsync();
        await loop.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(loop.CompletedRounds >= done);
    }

    [Fact]
    public void Start_twice_is_rejected()
    {
        using var cts = new CancellationTokenSource();
        using var loop = new WorkLoop();
        loop.Start(cts.Token);
        Assert.Throws<InvalidOperationException>(() => loop.Start(cts.Token));
    }

    [Fact]
    public void Task_before_Start_is_rejected()
    {
        using var loop = new WorkLoop();
        Assert.Throws<InvalidOperationException>(() => { _ = loop.Task; });   // 块体 lambda：表达式体会绑到 Func<Task> 重载（CS0619）
    }

    [Fact]
    public async Task An_exception_inside_one_round_does_not_kill_the_loop()
    {
        using var cts = new CancellationTokenSource();
        using var loop = new WorkLoop { ThrowOnRound = 1 };     // 测试缝：第 N 轮抛出
        loop.Start(cts.Token);

        await loop.WaitForRoundAsync(TimeSpan.FromSeconds(10));
        Assert.True(loop.CompletedRounds >= 1);
        Assert.Equal(1, loop.FaultedRounds);

        // 出错的那一轮之后循环还在跑：再等到一个完整的正常轮次。
        await loop.WaitForRoundAsync(TimeSpan.FromSeconds(10));
        Assert.True(loop.CompletedRounds >= 2);
        Assert.False(loop.Task.IsCompleted);

        await cts.CancelAsync();
        await loop.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(loop.Task.IsCompletedSuccessfully);
    }
}
