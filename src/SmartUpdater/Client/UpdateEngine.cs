using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// <see cref="IUpdateEngine"/> 的生产实现：把 Apply 层的组件各转发一次，不含业务判断（判断在 UpdateCycle / StartupRunner）。
/// 这是 Client 层里唯一直接调用 Apply 层组件的文件。
/// </summary>
internal sealed class UpdateEngine : IUpdateEngine
{
    private readonly UpdateEnvironment _env;
    private readonly string _mainExecutableRelativePath;
    private readonly FileLogger? _fileLogger;
    private readonly ReportQueue _reportQueue;

    /// <param name="layout">安装与数据目录布局。</param>
    /// <param name="mainExecutableRelativePath"><c>ProcessIdentity.RelativeExecutablePath(layout.InstallDirectory)</c>（正斜杠）。</param>
    /// <param name="env">环境依赖。</param>
    /// <param name="log">更新日志。</param>
    /// <param name="fileLogger">文件日志；关闭文件日志时为 null。Dispose 时释放它。</param>
    public UpdateEngine(UpdateLayout layout, string mainExecutableRelativePath, UpdateEnvironment env, IUpdateLog log, FileLogger? fileLogger)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainExecutableRelativePath);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(log);

        Layout = layout;
        Log = log;
        _mainExecutableRelativePath = mainExecutableRelativePath;
        _env = env;
        _fileLogger = fileLogger;
        _reportQueue = new ReportQueue(layout.ReportsFile, env.FileOperations, env.TimeProvider, log);
    }

    /// <inheritdoc />
    public UpdateLayout Layout { get; }

    /// <inheritdoc />
    public IUpdateLog Log { get; }

    /// <inheritdoc />
    public UpdateState LoadState() => new UpdateStateStore(Layout.StateFile, _env.FileOperations, Log).Load();

    /// <inheritdoc />
    public void SaveState(UpdateState state) => new UpdateStateStore(Layout.StateFile, _env.FileOperations, Log).Save(state);

    /// <inheritdoc />
    public Version? ReadInstalledVersion()
    {
        try
        {
            return AtomicFile.ReadJson(_env.FileOperations, Layout.ManifestFile, SmartUpdaterJsonContext.Default.PackageManifest)?.Version;
        }
        catch (JsonException ex)
        {
            Log.Warning(null, $"已安装的 manifest 无法解析：{ex.Message}", ex);
            return null;
        }
    }

    /// <inheritdoc />
    public InstallDirectoryProbeResult ProbeInstallDirectory() => InstallDirectoryProbe.Probe(Layout.StateDirectory);

    /// <inheritdoc />
    public PackageSummary ReadPackage(string packagePath)
    {
        try
        {
            using PackageContents contents = PackageReader.Open(packagePath);
            return new PackageSummary(contents.Manifest.Version, contents.TotalFileBytes);
        }
        catch (PackageFormatException ex)
        {
            throw new UpdateFailedException(UpdateStage.Verify, $"升级包格式不合法：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public DiskSpaceCheckResult CheckDiskSpace(long packageBytes, long totalFileBytes)
        => DiskSpaceChecker.Check(packageBytes, totalFileBytes, Layout.InstallDirectory, Layout.DownloadCacheDirectory, DiskSpaceChecker.GetAvailableBytes);

    /// <inheritdoc />
    public ApplyResult Apply(string packagePath, Version currentVersion, IProgress<UpdateProgressInfo>? progress, CancellationToken ct)
    {
        var applier = new PackageApplier(Layout, _env.FileOperations, Log, _env.TimeProvider);
        var request = new ApplyRequest(packagePath, currentVersion, _mainExecutableRelativePath);
        try
        {
            return applier.Apply(request, progress is null ? null : new ProgressAdapter(progress), ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new UpdateFailedException(UpdateStage.Commit, "存在未完成的更新事务，需先执行启动恢复", ex);
        }
    }

    /// <inheritdoc />
    public RecoveryResult RunRecovery() => new UpdateRecovery(Layout, _env.FileOperations, Log).Run();

    /// <inheritdoc />
    public RecoveryResult RollbackFromBackups() => new UpdateRecovery(Layout, _env.FileOperations, Log).RollbackFromBackups();

    /// <inheritdoc />
    public void DeleteJournal() => new JournalStore(Layout.JournalFile, _env.FileOperations, Log).Delete();

    /// <inheritdoc />
    public long? GetAvailableDiskBytes() => DiskSpaceChecker.GetAvailableBytes(Layout.InstallDirectory);

    /// <inheritdoc />
    public string ReadLogTail() => LogTail.Read(_fileLogger?.CurrentFilePath);

    /// <inheritdoc />
    public void EnqueueReport(UpdateReport report) => _reportQueue.Enqueue(report);

    /// <inheritdoc />
    public Task<int> FlushReportsAsync(IUpdateReporter reporter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        return _reportQueue.FlushAsync(reporter.SendAsync, ct);
    }

    /// <inheritdoc />
    public void Dispose() => _fileLogger?.Dispose();

    /// <summary>把 <see cref="ApplyProgress"/> 同步转成 <see cref="UpdateProgressInfo"/>，线程切换交给下游的 <see cref="IProgress{T}"/>。</summary>
    private sealed class ProgressAdapter(IProgress<UpdateProgressInfo> inner) : IProgress<ApplyProgress>
    {
        public void Report(ApplyProgress value) => inner.Report(new UpdateProgressInfo(value.Stage, value.BytesProcessed, value.TotalBytes));
    }
}
