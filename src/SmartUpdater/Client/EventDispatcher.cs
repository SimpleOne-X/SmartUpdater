namespace SimpleOneX.SmartUpdater;

/// <summary>把事件封送回捕获的 SynchronizationContext（没有则内联调用），并隔离每个处理器抛出的异常。</summary>
internal sealed class EventDispatcher
{
    private readonly SynchronizationContext? _context;
    private readonly IUpdateLog _log;

    public EventDispatcher(SynchronizationContext? context, IUpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _log = log;
    }

    public bool HasContext => _context is not null;

    /// <summary>
    /// handler 为 null 立即完成；无 context 时在当前线程逐个调用；否则 Post 到 context，处理器全部返回后完成。
    /// 处理器异常记 Error 后吞掉，返回的 Task 永远正常完成。
    /// </summary>
    public Task InvokeAsync<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args)
        where TArgs : EventArgs
    {
        if (handler is null)
        {
            return Task.CompletedTask;
        }

        if (_context is null)
        {
            InvokeIsolated(handler, sender, args, _log);
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _context.Post(
                _ =>
                {
                    try
                    {
                        InvokeIsolated(handler, sender, args, _log);
                    }
                    finally
                    {
                        tcs.TrySetResult();
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            _log.Error(null, "向同步上下文投递事件失败，改为在当前线程调用", ex);
            InvokeIsolated(handler, sender, args, _log);
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    /// <summary>同 <see cref="InvokeAsync{TArgs}"/> 但不等待；无 context 时同步调用。</summary>
    public void Post<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args)
        where TArgs : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        if (_context is null)
        {
            InvokeIsolated(handler, sender, args, _log);
            return;
        }

        try
        {
            _context.Post(_ => InvokeIsolated(handler, sender, args, _log), null);
        }
        catch (Exception ex)
        {
            _log.Error(null, "向同步上下文投递事件失败，改为在当前线程调用", ex);
            InvokeIsolated(handler, sender, args, _log);
        }
    }

    private static void InvokeIsolated<TArgs>(EventHandler<TArgs> handler, object sender, TArgs args, IUpdateLog log)
        where TArgs : EventArgs
    {
        foreach (Delegate d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)d)(sender, args);
            }
            catch (Exception ex)
            {
                log.Error(null, $"事件处理器 {d.Method.DeclaringType?.FullName}.{d.Method.Name} 抛出异常，已忽略", ex);
            }
        }
    }
}
