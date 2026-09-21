namespace SimpleOneX.SmartUpdater;

/// <summary>升级包的摘要：版本与全部文件的总字节数。</summary>
/// <param name="Version">包内 manifest 的版本。</param>
/// <param name="TotalFileBytes">包内全部文件的总字节数。</param>
internal readonly record struct PackageSummary(Version Version, long TotalFileBytes);

/// <summary>应用阶段的进度。</summary>
/// <param name="Stage">所处阶段。</param>
/// <param name="Processed">已处理量。</param>
/// <param name="Total">总量。</param>
internal readonly record struct UpdateProgressInfo(UpdateStage Stage, long Processed, long Total);

/// <summary>更新底层组件（Apply 层）的门面。生产实现 UpdateEngine 是唯一直接调用这些组件的文件；测试用 FakeUpdateEngine。</summary>
internal interface IUpdateEngine : IDisposable
{
    /// <summary>安装与数据目录布局。</summary>
    UpdateLayout Layout { get; }

    /// <summary>更新日志。</summary>
    IUpdateLog Log { get; }

    /// <summary>读取状态；永不抛出（UpdateStateStore.Load 的契约）。</summary>
    UpdateState LoadState();

    /// <summary>保存状态；IO 异常原样抛出，调用方记日志。</summary>
    void SaveState(UpdateState state);

    /// <summary>.smartupdater/manifest.json 的 version（原样，未规范化）；不存在或损坏返回 null。</summary>
    Version? ReadInstalledVersion();

    /// <summary>探测安装目录是否可写。</summary>
    InstallDirectoryProbeResult ProbeInstallDirectory();

    /// <summary>读取升级包摘要；格式问题抛 UpdateFailedException(Verify)。</summary>
    PackageSummary ReadPackage(string packagePath);

    /// <summary>检查磁盘空间。</summary>
    DiskSpaceCheckResult CheckDiskSpace(long packageBytes, long totalFileBytes);

    /// <summary>
    /// PackageApplier.Apply 的门面（同步；调用方用 Task.Run 包起来）。
    /// 已有 journal 抛 UpdateFailedException(Commit)；其他失败按 PackageApplier 的 Stage 原样抛出。
    /// </summary>
    ApplyResult Apply(string packagePath, Version currentVersion, IProgress<UpdateProgressInfo>? progress, CancellationToken ct);

    /// <summary>运行崩溃恢复。</summary>
    RecoveryResult RunRecovery();

    /// <summary>从备份回滚。</summary>
    RecoveryResult RollbackFromBackups();

    /// <summary>删除 journal。</summary>
    void DeleteJournal();

    /// <summary>安装卷的可用字节数；未知返回 null。</summary>
    long? GetAvailableDiskBytes();

    /// <summary>日志末尾内容；无日志返回空字符串。</summary>
    string ReadLogTail();

    /// <summary>把一条上报放进补报队列。</summary>
    void EnqueueReport(UpdateReport report);

    /// <summary>把队列里的上报依次送出，返回成功条数。</summary>
    Task<int> FlushReportsAsync(IUpdateReporter reporter, CancellationToken ct);
}
