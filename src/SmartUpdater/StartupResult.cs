namespace SimpleOneX.SmartUpdater;

/// <summary>
/// <see cref="SmartUpdaterApp.Run(string[])"/> 的结果：本次启动的更新状态与单实例入口。
/// </summary>
public sealed class StartupResult
{
    internal StartupResult(
        bool justUpdated,
        Version? fromVersion,
        Version? toVersion,
        Version currentVersion,
        string appId,
        bool isUpdateEnabled,
        string? updateDisabledReason,
        bool isRollbackApplied)
    {
        JustUpdated = justUpdated;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        CurrentVersion = currentVersion;
        AppId = appId;
        IsUpdateEnabled = isUpdateEnabled;
        UpdateDisabledReason = updateDisabledReason;
        IsRollbackApplied = isRollbackApplied;
    }

    /// <summary>本次启动是上次更新的首次启动（更新事务已完成的记录已在启动时被清理）。</summary>
    public bool JustUpdated { get; }

    /// <summary><see cref="JustUpdated"/> 为 true 时的旧版本（四段）；否则为 null。</summary>
    public Version? FromVersion { get; }

    /// <summary><see cref="JustUpdated"/> 为 true 时的新版本（四段）；否则为 null。</summary>
    public Version? ToVersion { get; }

    /// <summary>当前版本（四段）。</summary>
    public Version CurrentVersion { get; }

    /// <summary>应用标识。</summary>
    public string AppId { get; }

    /// <summary>更新功能是否可用：安装目录可写，且启动收尾没有出现意外异常。</summary>
    public bool IsUpdateEnabled { get; }

    /// <summary><see cref="IsUpdateEnabled"/> 为 false 时的原因；否则为 null。</summary>
    public string? UpdateDisabledReason { get; }

    /// <summary>本次启动是否处理了 <c>--smartupdater-rollback</c>。</summary>
    public bool IsRollbackApplied { get; }

    /// <summary>
    /// 尝试成为唯一实例：创建命名 Mutex <c>&lt;name&gt;.&lt;AppId&gt;</c>（<c>Local\</c> 作用域）。
    /// </summary>
    /// <param name="name">实例名；不能为空白，也不能含反斜杠。</param>
    /// <returns>成功时返回句柄（Dispose 即释放）；已有实例在运行时返回 null。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白或含反斜杠。</exception>
    public SingleInstanceHandle? TryAcquireSingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("名字不能含反斜杠；需要 Global\\ 作用域请自行创建 Mutex", nameof(name));
        }

        string fullName = $"{name}.{AppId}";
        SingleInstanceHandle? handle = SingleInstanceHandle.TryAcquire(fullName);
        if (handle is null)
        {
            SmartUpdaterApp.RememberRejectedInstance(fullName);
        }

        return handle;
    }
}

/// <summary>
/// 单实例句柄：持有命名 Mutex 与激活事件。Dispose 后名字可被再次获取。
/// </summary>
public sealed class SingleInstanceHandle : IDisposable
{
    /// <summary>激活事件名相对于 Mutex 名的后缀。</summary>
    public const string ActivationSuffix = ".activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle _registration;
    private int _disposed;

    private SingleInstanceHandle(string name, Mutex mutex, EventWaitHandle activation)
    {
        Name = name;
        _mutex = mutex;
        _activation = activation;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            activation,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    RaiseActivationRequested();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>完整的 Mutex 名。</summary>
    public string Name { get; }

    /// <summary>另一实例调用 <see cref="SmartUpdaterApp.ActivateExistingInstance"/> 时，在线程池线程上触发。</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// 释放事件等待、命名事件与 Mutex；幂等。不调用 ReleaseMutex：
    /// Mutex 由创建它的线程所有，Dispose 可能发生在别的线程，ReleaseMutex 会抛异常，而关闭句柄本身就足以让名字可被再次获取。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registration.Unregister(null);
        _activation.Dispose();
        _mutex.Dispose();
    }

    internal static SingleInstanceHandle? TryAcquire(string fullName)
    {
        var mutex = new Mutex(initiallyOwned: true, fullName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, fullName + ActivationSuffix, out _);
        return new SingleInstanceHandle(fullName, mutex, activation);
    }

    private void RaiseActivationRequested()
    {
        EventHandler? handlers = ActivationRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 消费方处理器的异常不能炸线程池，也不能让后面的处理器收不到通知；这里没有日志可记，只能隔离。
            }
        }
    }
}
