namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>
/// 示例的"业务工作循环"：每轮做一件可观测但无副作用的小事（计数），然后等 200 ms。
/// 它存在的意义是演示收尾契约：循环的 <see cref="Task"/> 被保存下来，
/// <c>Restarting</c> 里先取消它、再 <c>e.WaitFor(task)</c>，包会等它真正结束后才交接给新进程。
/// </summary>
internal sealed class WorkLoop : IDisposable
{
    private static readonly TimeSpan s_roundDelay = TimeSpan.FromMilliseconds(200);

    private TaskCompletionSource _roundDone = NewSignal();
    private Task? _task;
    private int _startedFlag;
    private int _completedRounds;
    private int _faultedRounds;

    /// <summary>已完成的轮数（含出错的轮）。</summary>
    public int CompletedRounds => Volatile.Read(ref _completedRounds);

    /// <summary>出错（被单轮 catch 住）的轮数。</summary>
    public int FaultedRounds => Volatile.Read(ref _faultedRounds);

    /// <summary>测试缝：第 N 轮（从 1 计）抛出异常。默认 null，不生效。</summary>
    internal int? ThrowOnRound { get; init; }

    /// <summary>循环的任务。取消后以 RanToCompletion 结束（不抛 <see cref="OperationCanceledException"/>）。</summary>
    /// <exception cref="InvalidOperationException">尚未 <see cref="Start"/>。</exception>
    public Task Task => Volatile.Read(ref _task) ?? throw new InvalidOperationException("WorkLoop 尚未启动。");

    /// <summary>启动循环。只能调用一次。</summary>
    /// <exception cref="InvalidOperationException">重复启动。</exception>
    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _startedFlag, 1) != 0)
        {
            throw new InvalidOperationException("WorkLoop 已经启动过。");
        }

        Volatile.Write(ref _task, Task.Run(() => RunAsync(ct), CancellationToken.None));
    }

    /// <summary>等到下一轮完成（事件驱动，无轮询、无 Thread.Sleep）。</summary>
    /// <param name="timeout">超时。</param>
    public Task WaitForRoundAsync(TimeSpan timeout) => Volatile.Read(ref _roundDone).Task.WaitAsync(timeout);

    /// <summary>无需释放任何资源；保留 IDisposable 是为了让调用方用 using 表达"循环的生命周期到此为止"。</summary>
    public void Dispose()
    {
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int round = Interlocked.Increment(ref _completedRounds);
                try
                {
                    if (ThrowOnRound == round)
                    {
                        throw new InvalidOperationException($"测试注入：第 {round} 轮失败。");
                    }
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref _faultedRounds);
                }

                Interlocked.Exchange(ref _roundDone, NewSignal()).TrySetResult();
                await Task.Delay(s_roundDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾：Task 必须是 RanToCompletion，否则 e.WaitFor(task) 会把取消当成失败。
        }
    }
}
