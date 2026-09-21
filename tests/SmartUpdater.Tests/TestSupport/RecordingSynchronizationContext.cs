namespace SmartUpdater.Tests;

/// <summary>队列式 SynchronizationContext，模拟 UI 线程：Post 只入队，RunAll 在调用线程上排空并把自己设为 Current。</summary>
internal sealed class RecordingSynchronizationContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int PostCount { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            PostCount++;
            _queue.Enqueue((d, state));
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("包不得用 Send，会死锁 UI 线程。");

    /// <summary>排空队列，返回执行的回调数。执行期间 SynchronizationContext.Current 为本实例。</summary>
    public int RunAll()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            int count = 0;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        return count;
                    }

                    item = _queue.Dequeue();
                }

                item.Callback(item.State);
                count++;
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>反复排空队列直到 completion 完成（真实时间的短轮询，默认守卫 10 s）。</summary>
    public async Task DrainUntilAsync(Task completion, TimeSpan? realTimeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (realTimeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            RunAll();
            if (completion.IsCompleted)
            {
                RunAll();
                await completion;
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待的任务在守卫时间内未完成。");
            }

            await Task.Delay(10);
        }
    }
}
