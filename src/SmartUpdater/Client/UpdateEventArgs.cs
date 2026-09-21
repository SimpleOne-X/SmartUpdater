namespace SimpleOneX.SmartUpdater;

/// <summary>消费方对 optional 更新的表态。</summary>
internal enum UpdateDecision
{
    Undecided = 0,
    Accept = 1,
    Postpone = 2,
    Skip = 3,
}

/// <summary><c>UpdateAvailable</c> 事件参数。只在 <see cref="UpdateMode.Optional"/> 时触发；必须在处理器返回前做出决定。</summary>
public sealed class UpdateAvailableEventArgs : EventArgs
{
    private UpdateDecision _decision;
    private bool _isSealed;

    internal UpdateAvailableEventArgs(ReleaseEntry release, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(currentVersion);
        Version = release.Version;
        CurrentVersion = currentVersion;
        Mode = release.Mode;
        Notes = release.Notes;
        PackageSize = release.Package.Size;
        ReleasedAt = release.ReleasedAt;
    }

    /// <summary>可安装的版本。</summary>
    public Version Version { get; }

    /// <summary>当前运行的版本。</summary>
    public Version CurrentVersion { get; }

    /// <summary>更新模式。</summary>
    public UpdateMode Mode { get; }

    /// <summary>更新说明。</summary>
    public string? Notes { get; }

    /// <summary>升级包字节数。</summary>
    public long PackageSize { get; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset ReleasedAt { get; }

    internal UpdateDecision Decision => _decision;

    /// <summary>现在更新。</summary>
    public void Accept() => Decide(UpdateDecision.Accept);

    /// <summary>稍后再说：本进程内不再询问，下次启动再问。</summary>
    public void Postpone() => Decide(UpdateDecision.Postpone);

    /// <summary>跳过这个版本：直到有更新的版本才再问。</summary>
    public void Skip() => Decide(UpdateDecision.Skip);

    internal void Seal() => _isSealed = true;

    private void Decide(UpdateDecision decision)
    {
        if (_isSealed)
        {
            throw new InvalidOperationException("必须在 UpdateAvailable 事件处理器返回之前调用 Accept / Postpone / Skip。");
        }

        if (_decision != UpdateDecision.Undecided)
        {
            throw new InvalidOperationException($"已经做出过决定（{_decision}），不能再改。");
        }

        _decision = decision;
    }
}

/// <summary><c>ProgressChanged</c> 事件参数。覆盖下载、校验、提交三个阶段。</summary>
public sealed class UpdateProgressEventArgs : EventArgs
{
    internal UpdateProgressEventArgs(UpdateStage stage, long bytesReceived, long totalBytes, double bytesPerSecond)
    {
        Stage = stage;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
        BytesPerSecond = bytesPerSecond;
    }

    /// <summary>所处阶段。</summary>
    public UpdateStage Stage { get; }

    /// <summary>百分比 0~100；总量未知时为 0。</summary>
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100);

    /// <summary>已处理字节数。</summary>
    public long BytesReceived { get; }

    /// <summary>总字节数。</summary>
    public long TotalBytes { get; }

    /// <summary>当前速度（字节 / 秒）；非下载阶段为 0。</summary>
    public double BytesPerSecond { get; }
}

/// <summary><c>Restarting</c> 事件参数：不可回头点。消费方在此取消工作循环、保存状态，并把要等待的任务交回。</summary>
public sealed class RestartingEventArgs : EventArgs
{
    private readonly List<Task> _pending = [];
    private bool _isSealed;

    internal RestartingEventArgs(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
    }

    /// <summary>即将启动的新版本。</summary>
    public Version Version { get; }

    internal IReadOnlyList<Task> PendingTasks => _pending;

    /// <summary>把收尾任务交给包统一等待（上限 <c>ShutdownTimeout</c>，默认 15 秒）。</summary>
    public void WaitFor(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_isSealed)
        {
            throw new InvalidOperationException("必须在 Restarting 事件处理器返回之前调用 WaitFor。");
        }

        _pending.Add(task);
    }

    internal void Seal() => _isSealed = true;
}

/// <summary><c>Failed</c> 事件参数。</summary>
public sealed class UpdateFailedEventArgs : EventArgs
{
    internal UpdateFailedEventArgs(UpdateStage stage, Exception exception, Version? version)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Stage = stage;
        Exception = exception;
        Version = version;
    }

    /// <summary>失败发生的阶段。</summary>
    public UpdateStage Stage { get; }

    /// <summary>失败原因。</summary>
    public Exception Exception { get; }

    /// <summary>本轮的目标版本；尚未选出目标时为 null。</summary>
    public Version? Version { get; }
}
