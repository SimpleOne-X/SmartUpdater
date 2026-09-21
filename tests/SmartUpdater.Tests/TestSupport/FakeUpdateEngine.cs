using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>内存版 IUpdateEngine。LoadState 返回副本、SaveState 写回，模拟真实存储的"必须重新加载才能看到别人写的字段"。</summary>
internal sealed class FakeUpdateEngine : IUpdateEngine
{
    public static readonly Guid DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public FakeUpdateEngine(string appId = "TestApp-00000000", string? installDirectory = null, string? localAppDataDirectory = null)
    {
        Layout = new UpdateLayout(installDirectory ?? @"C:\fake\install\app", localAppDataDirectory ?? @"C:\fake\localappdata", appId);
        PackageReader = path => new PackageSummary(VersionFromPath(path), 1000);
        DiskCheck = (packageBytes, totalFileBytes) => new DiskSpaceCheckResult(
            new VolumeCheck(@"C:\", packageBytes + totalFileBytes, AvailableDiskBytes), null);
        ApplyBehavior = (path, currentVersion, progress, _) =>
        {
            PackageSummary summary = PackageReader(path);
            progress?.Report(new UpdateProgressInfo(UpdateStage.Commit, 1000, 1000));
            State.CurrentVersion = summary.Version;      // 应用器在 done 之后写 state.currentVersion
            return new ApplyResult(currentVersion, summary.Version, 1, 0, 0);
        };
    }

    public UpdateLayout Layout { get; }

    public RecordingLog RecordingLog { get; } = new();

    public IUpdateLog Log => RecordingLog;

    public UpdateState State { get; set; } = new() { DeviceGuid = DeviceGuid, CurrentVersion = new Version(1, 2, 3, 0) };

    public int SaveCount { get; private set; }

    public Exception? SaveException { get; set; }

    public Version? InstalledVersion { get; set; }

    public InstallDirectoryProbeResult ProbeResult { get; set; } = new(true, null);

    public Func<string, PackageSummary> PackageReader { get; set; }

    public Func<long, long, DiskSpaceCheckResult> DiskCheck { get; set; }

    public Func<string, Version, IProgress<UpdateProgressInfo>?, CancellationToken, ApplyResult> ApplyBehavior { get; set; }

    public List<(string PackagePath, Version CurrentVersion)> ApplyCalls { get; } = [];

    public RecoveryResult Recovery { get; set; } = RecoveryResult.None;

    /// <summary>非 null 时 RunRecovery 抛出它（模拟组件的意外异常）。</summary>
    public Exception? RecoveryException { get; set; }

    public int RecoveryCalls { get; private set; }

    public RecoveryResult RollbackResult { get; set; } = RecoveryResult.None;

    public int RollbackFromBackupsCalls { get; private set; }

    public int DeleteJournalCalls { get; private set; }

    public long? AvailableDiskBytes { get; set; } = 50_000_000_000;

    public string LogTail { get; set; } = "last log lines";

    public List<UpdateReport> Queued { get; } = [];

    public List<UpdateReport> Sent { get; } = [];

    public bool IsDisposed { get; private set; }

    public static Version VersionFromPath(string path)
        => Version.Parse(Path.GetFileNameWithoutExtension(path));

    public UpdateState LoadState() => Copy(State);

    public void SaveState(UpdateState state)
    {
        if (SaveException is not null)
        {
            throw SaveException;
        }

        SaveCount++;
        State = Copy(state);
    }

    private static UpdateState Copy(UpdateState source) => new()
    {
        CurrentVersion = source.CurrentVersion,
        DeviceGuid = source.DeviceGuid,
        SkippedVersions = [.. source.SkippedVersions],
        FeedETag = source.FeedETag,
        LastCheckedAt = source.LastCheckedAt,
        LastReportedAt = source.LastReportedAt,
        ChainLength = source.ChainLength,
        ChainLimitFeedETag = source.ChainLimitFeedETag,
    };

    public Version? ReadInstalledVersion() => InstalledVersion;

    public InstallDirectoryProbeResult ProbeInstallDirectory() => ProbeResult;

    public PackageSummary ReadPackage(string packagePath) => PackageReader(packagePath);

    public DiskSpaceCheckResult CheckDiskSpace(long packageBytes, long totalFileBytes) => DiskCheck(packageBytes, totalFileBytes);

    public ApplyResult Apply(string packagePath, Version currentVersion, IProgress<UpdateProgressInfo>? progress, CancellationToken ct)
    {
        ApplyCalls.Add((packagePath, currentVersion));
        return ApplyBehavior(packagePath, currentVersion, progress, ct);
    }

    public RecoveryResult RunRecovery()
    {
        RecoveryCalls++;
        if (RecoveryException is not null)
        {
            throw RecoveryException;
        }

        return Recovery;
    }

    public RecoveryResult RollbackFromBackups()
    {
        RollbackFromBackupsCalls++;
        return RollbackResult;
    }

    public void DeleteJournal() => DeleteJournalCalls++;

    public long? GetAvailableDiskBytes() => AvailableDiskBytes;

    public string ReadLogTail() => LogTail;

    public void EnqueueReport(UpdateReport report) => Queued.Add(report);

    public async Task<int> FlushReportsAsync(IUpdateReporter reporter, CancellationToken ct)
    {
        int sent = 0;
        while (Queued.Count > 0)
        {
            UpdateReport next = Queued[0];
            if (!await reporter.SendAsync(next, ct))
            {
                break;
            }

            Queued.RemoveAt(0);
            Sent.Add(next);
            sent++;
        }

        return sent;
    }

    public void Dispose() => IsDisposed = true;
}
