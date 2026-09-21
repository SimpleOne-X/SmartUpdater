namespace SmartUpdater.Tests;

/// <summary>
/// 完全由测试驱动的时钟：GetUtcNow / GetTimestamp / CreateTimer 都不看真实时间。
/// Task.Delay(…, provider)、Task.WaitAsync(…, provider)、PeriodicTimer(…, provider)、CancellationTokenSource(…, provider)
/// 都通过 CreateTimer 工作（实测），所以 Advance 能确定性地推进被测代码里的全部等待。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<(int Minimum, TaskCompletionSource Signal)> _waiters = [];
    private DateTimeOffset _now;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    /// <summary>当前已武装（有到期时间）的计时器数量。</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>把时钟推进 delta，按到期顺序触发途中到期的计时器（回调在调用线程上同步执行）。</summary>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                foreach (ManualTimer t in _timers)
                {
                    if (t.Due is { } due && due <= target && (next is null || due < next.Due!.Value))
                    {
                        next = t;
                    }
                }

                if (next is null)
                {
                    _now = target;
                    break;
                }

                _now = next.Due!.Value;
                next.Rearm();
            }

            next.Fire();
        }
    }

    /// <summary>等待直到至少有 minimumActiveTimers 个计时器被武装（被测代码进入了 Task.Delay 之类的等待）。真实时间守卫默认 10 s。</summary>
    public Task WaitForTimersAsync(int minimumActiveTimers, TimeSpan? realTimeout = null)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_timers.Count(t => t.Due is not null) >= minimumActiveTimers)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((minimumActiveTimers, signal));
        }

        return signal.Task.WaitAsync(realTimeout ?? TimeSpan.FromSeconds(10));
    }

    private void NotifyWaiters()
    {
        // 调用方持锁
        int active = _timers.Count(t => t.Due is not null);
        for (int i = _waiters.Count - 1; i >= 0; i--)
        {
            if (active >= _waiters[i].Minimum)
            {
                _waiters[i].Signal.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            lock (owner._gate)
            {
                owner._timers.Add(this);
            }
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                _owner.NotifyWaiters();
            }

            return true;
        }

        public void Rearm()
        {
            // 调用方持锁
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : _owner._now + _period;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
