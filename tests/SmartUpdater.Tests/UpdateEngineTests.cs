using System.Collections.Concurrent;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateEngineTests
{
    private static UpdateEnvironment Env(InstallationFixture f, ManualTimeProvider? time = null) => new()
    {
        InstallDirectory = f.Layout.InstallDirectory,
        LocalApplicationDataDirectory = f.Temp.Resolve("appdata"),
        TimeProvider = time ?? new ManualTimeProvider(),
        MachineName = "PC-01",
        OsVersion = "Microsoft Windows NT 10.0.26200.0",
    };

    private static UpdateEngine Engine(InstallationFixture f, RecordingLog? log = null, FileLogger? fileLogger = null)
        => new(f.Layout, "app.exe", Env(f), log ?? new RecordingLog(), fileLogger);

    [Fact]
    public void State_round_trips_through_the_store()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);

        UpdateState state = engine.LoadState();
        Assert.Equal(new Version(1, 0, 0), state.CurrentVersion);
        Assert.Equal(InstallationFixture.DeviceGuid, state.DeviceGuid);

        state.FeedETag = "\"e1\"";
        engine.SaveState(state);

        Assert.Equal("\"e1\"", f.ReadState().FeedETag);
        Assert.Equal("\"e1\"", engine.LoadState().FeedETag);
    }

    [Fact]
    public void ReadInstalledVersion_reads_the_manifest_as_written_and_tolerates_corruption()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        var log = new RecordingLog();
        using UpdateEngine engine = Engine(f, log);

        Assert.Equal(new Version(1, 0, 0), engine.ReadInstalledVersion());

        File.WriteAllText(f.Layout.ManifestFile, "{ broken");
        Assert.Null(engine.ReadInstalledVersion());
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Warning));

        File.Delete(f.Layout.ManifestFile);
        Assert.Null(engine.ReadInstalledVersion());
    }

    [Fact]
    public void Probe_reports_a_writable_install_directory()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);

        InstallDirectoryProbeResult probe = engine.ProbeInstallDirectory();

        Assert.True(probe.IsWritable);
        Assert.Null(probe.FailureReason);
    }

    [Fact]
    public void ReadPackage_returns_version_and_total_bytes_and_maps_format_errors_to_Verify()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        PackageSummary summary = engine.ReadPackage(zip);

        Assert.Equal(new Version(1, 1, 0), summary.Version);
        Assert.True(summary.TotalFileBytes > 0);

        string garbage = f.Temp.Resolve("garbage.zip");
        File.WriteAllText(garbage, "not a zip");
        var ex = Assert.Throws<UpdateFailedException>(() => engine.ReadPackage(garbage));
        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.IsType<PackageFormatException>(ex.InnerException);
    }

    [Fact]
    public void CheckDiskSpace_uses_the_real_volume_and_is_sufficient_for_tiny_packages()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);

        DiskSpaceCheckResult result = engine.CheckDiskSpace(packageBytes: 1000, totalFileBytes: 1000);

        Assert.True(result.IsSufficient);
        Assert.True(result.RequiredBytes >= 2000);
        Assert.NotNull(engine.GetAvailableDiskBytes());
    }

    [Fact]
    public void Apply_installs_the_package_reports_progress_and_updates_state()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var progress = new ConcurrentQueue<UpdateProgressInfo>();

        ApplyResult result = engine.Apply(zip, new Version(1, 0, 0, 0), new Progress<UpdateProgressInfo>(progress.Enqueue), CancellationToken.None);

        Assert.Equal(new Version(1, 0, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        f.AssertStandardV110Files();
        Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
        Assert.Equal(JournalStatus.Valid, f.ReadJournal().Status);
        Assert.Equal(JournalState.Done, f.ReadJournal().Journal!.State);
        // Progress<T> 在无同步上下文时经线程池回调，等一下再断言
        SpinWait.SpinUntil(() => progress.ToArray().Any(p => p.Stage == UpdateStage.Commit && p.Processed == p.Total), TimeSpan.FromSeconds(5));
        UpdateProgressInfo[] seen = progress.ToArray();
        Assert.Contains(seen, p => p.Stage == UpdateStage.Commit);
        Assert.Contains(seen, p => p.Stage == UpdateStage.Verify);
    }

    [Fact]
    public void Apply_with_a_pending_journal_fails_at_Commit_stage()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        engine.Apply(zip, new Version(1, 0, 0, 0), null, CancellationToken.None);       // 留下 done journal

        var ex = Assert.Throws<UpdateFailedException>(() => engine.Apply(zip, new Version(1, 1, 0, 0), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void RunRecovery_after_apply_completes_the_update_and_cleans_backups()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        engine.Apply(zip, new Version(1, 0, 0, 0), null, CancellationToken.None);
        Assert.NotEmpty(f.Leftovers());

        RecoveryResult recovery = engine.RunRecovery();

        Assert.Equal(RecoveryAction.CompletedUpdate, recovery.Action);
        Assert.True(recovery.JustUpdated);
        Assert.Equal(new Version(1, 1, 0), recovery.ToVersion);
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Empty(f.CacheFiles());
        Assert.Equal(RecoveryAction.None, engine.RunRecovery().Action);
    }

    [Fact]
    public void RollbackFromBackups_and_DeleteJournal_restore_the_old_version()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        engine.Apply(zip, new Version(1, 0, 0, 0), null, CancellationToken.None);

        RecoveryResult rollback = engine.RollbackFromBackups();
        engine.DeleteJournal();

        Assert.Empty(rollback.Errors);
        Assert.Equal("exe-v1", f.Read("app.exe"));
        Assert.Equal("old", f.Read("old.dll"));
        Assert.Equal(new Version(1, 0, 0), f.ReadManifest()!.Version);
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Equal(new Version(1, 0, 0), engine.ReadInstalledVersion());
    }

    [Fact]
    public async Task Reports_are_queued_on_disk_and_flushed_through_the_reporter()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        var reporter = new RecordingReporter();
        UpdateReport report = UpdateReportBuilder.Heartbeat(InstallationFixture.DeviceGuid, Env(f), new Version(1, 0, 0, 0), 5, new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        engine.EnqueueReport(report);
        Assert.True(File.Exists(f.Layout.ReportsFile));

        int sent = await engine.FlushReportsAsync(reporter, TestContext.Current.CancellationToken);

        Assert.Equal(1, sent);
        Assert.Equal(UpdateEventType.Heartbeat, Assert.Single(reporter.Received).EventType);
        Assert.False(File.Exists(f.Layout.ReportsFile));
    }

    [Fact]
    public async Task Rejected_reports_stay_queued()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        using UpdateEngine engine = Engine(f);
        var reporter = new RecordingReporter { Accept = _ => false };
        // ReportedAt 取 ManualTimeProvider 的缺省 now，避免入队即被 7 天过期剪枝
        engine.EnqueueReport(UpdateReportBuilder.Heartbeat(InstallationFixture.DeviceGuid, Env(f), new Version(1, 0, 0, 0), null, new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)));

        int sent = await engine.FlushReportsAsync(reporter, TestContext.Current.CancellationToken);

        Assert.Equal(0, sent);
        Assert.True(File.Exists(f.Layout.ReportsFile));
    }

    [Fact]
    public void ReadLogTail_returns_the_file_logger_tail_or_empty()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        var time = new ManualTimeProvider();
        var fileLogger = new FileLogger(f.Layout.LogDirectory, f.Layout.FallbackLogDirectory, UpdateLogLevel.Information, time);
        using (UpdateEngine engine = Engine(f, fileLogger: fileLogger))
        {
            fileLogger.Write(UpdateLogLevel.Error, UpdateStage.Commit, "写入 app.exe 失败");

            Assert.Contains("写入 app.exe 失败", engine.ReadLogTail(), StringComparison.Ordinal);
        }

        using UpdateEngine noLog = Engine(f);
        Assert.Equal("", noLog.ReadLogTail());
    }

    [Fact]
    public void Dispose_releases_the_file_logger()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        var fileLogger = new FileLogger(f.Layout.LogDirectory, f.Layout.FallbackLogDirectory, UpdateLogLevel.Information, new ManualTimeProvider());
        fileLogger.Write(UpdateLogLevel.Information, null, "x");
        string path = fileLogger.CurrentFilePath!;

        Engine(f, fileLogger: fileLogger).Dispose();

        File.Move(path, path + ".moved");     // 句柄已释放才能改名
        Assert.True(File.Exists(path + ".moved"));
    }
}
