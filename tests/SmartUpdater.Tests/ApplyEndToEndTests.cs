using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ApplyEndToEndTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero));

    private static PackageApplier Applier(InstallationFixture f, IFileOperations? fs = null, IUpdateLog? log = null)
        => new(f.Layout, fs ?? Physical, log ?? new RecordingLog(), Clock);

    private static UpdateRecovery Recovery(InstallationFixture f, IFileOperations? fs = null, IUpdateLog? log = null)
        => new(f.Layout, fs ?? Physical, log ?? new RecordingLog());

    private static string SnapshotOfEverything(InstallationFixture f)
        => string.Join("\n", Directory.EnumerateFiles(f.Temp.Root, "*", SearchOption.AllDirectories)
            .Select(p => $"{Path.GetRelativePath(f.Temp.Root, p)}={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}")
            .Order(StringComparer.Ordinal));

    /// <summary>崩溃后要么完整旧版本、要么完整新版本，绝无中间态。</summary>
    private static void AssertConverged(InstallationFixture f, string context)
    {
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Empty(f.Leftovers());

        if (f.Read("app.exe") == "exe-v2")
        {
            f.AssertStandardV110Files();
            Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
            Assert.Empty(f.CacheFiles());
        }
        else
        {
            f.AssertStandardV1();
            Assert.Equal(new[] { "1.1.0.zip" }, f.CacheFiles());   // 未完成的更新不动缓存
        }
    }

    private static void RunApplyCrashingAt(InstallationFixture f, string zip, int n, out FaultInjectingFileOperations fs)
    {
        fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(n) };
        try
        {
            Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is SimulatedCrashException or UpdateFailedException or InvalidOperationException)
        {
            // 崩溃点之后的一切失败都是"磁盘不再响应"的表现
        }
    }

    [Fact]
    public void Full_cycle_is_reconstructible_from_the_log_file()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        string logFile;

        using (var logger = new FileLogger(f.Layout.LogDirectory, f.Layout.FallbackLogDirectory, UpdateLogLevel.Debug, Clock))
        {
            logFile = logger.CurrentFilePath!;
            ApplyResult applied = Applier(f, log: logger).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);
            Assert.Equal(new Version(1, 1, 0), applied.ToVersion);

            RecoveryResult startup = Recovery(f, log: logger).Run();
            Assert.True(startup.JustUpdated);
            Assert.Equal(new Version(1, 0, 0), startup.FromVersion);
            Assert.Equal(new Version(1, 1, 0), startup.ToVersion);

            Assert.Equal(RecoveryAction.None, Recovery(f, log: logger).Run().Action);
        }

        AssertConverged(f, "full cycle");
        Assert.Equal("exe-v2", f.Read("app.exe"));

        string log = File.ReadAllText(logFile, Encoding.UTF8);
        foreach (string expected in new[]
        {
            "write app.exe", "write new.dll", "delete old.dll", "skip settings.json",
            "(无) → preparing", "preparing → committing", "committing → done",
            "发现 journal：done", "更新已完成", "journal 已删除",
        })
        {
            Assert.Contains(expected, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Crash_at_every_disk_operation_during_apply_converges_after_startup_recovery()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using InstallationFixture f = InstallationFixture.StandardV1();
            string zip = f.StagePackage(InstallationFixture.StandardV110());

            RunApplyCrashingAt(f, zip, n, out FaultInjectingFileOperations fs);

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            string crashedAt = fs.Operations[^1].ToString();
            Recovery(f).Run();
            AssertConverged(f, crashedAt);
        }

        Assert.True(totalSteps >= 40, $"只扫描到 {totalSteps} 步，少于预期的改动操作加查询操作总数");
    }

    [Fact]
    public void Crash_during_recovery_after_a_crash_during_apply_still_converges()
    {
        // 先跑一次干净的应用，找出三个有代表性崩溃点的序号
        int commitStart;
        int executableGap;
        int afterDone;
        using (InstallationFixture probe = InstallationFixture.StandardV1())
        {
            string probeZip = probe.StagePackage(InstallationFixture.StandardV110());
            var recorder = new FaultInjectingFileOperations(Physical);
            Applier(probe, recorder).Apply(InstallationFixture.StandardRequest(probeZip), null, CancellationToken.None);

            commitStart = recorder.Operations.First(o => o.Kind == "Move" && o.SecondPath!.EndsWith(SwapFileNames.OldSuffix, StringComparison.Ordinal)).Index;
            executableGap = recorder.Operations.First(o => o.Kind == "Move" && o.Path.EndsWith("app.exe" + SwapFileNames.NewSuffix, StringComparison.Ordinal)).Index;
            afterDone = recorder.Operations.First(o => o.Kind == "Delete" && o.Path.EndsWith("state.json" + AtomicFile.TempSuffix, StringComparison.Ordinal)).Index;
        }

        foreach (int applyCrash in new[] { commitStart, executableGap, afterDone })
        {
            for (int m = 1; ; m++)
            {
                using InstallationFixture f = InstallationFixture.StandardV1();
                string zip = f.StagePackage(InstallationFixture.StandardV110());
                RunApplyCrashingAt(f, zip, applyCrash, out _);

                var recoveryFs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(m) };
                try
                {
                    Recovery(f, recoveryFs).Run();
                }
                catch (SimulatedCrashException)
                {
                }

                if (!recoveryFs.Crashed)
                {
                    Assert.True(m > 3, $"应用崩溃于第 {applyCrash} 步后，恢复只有 {m - 1} 步");
                    break;
                }

                Recovery(f).Run();
                AssertConverged(f, $"apply@{applyCrash} recovery@{m}");
            }
        }
    }

    [Fact]
    public void Manually_deleted_file_is_repaired_by_reapplying_the_same_version()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);
        Assert.True(Recovery(f).Run().JustUpdated);
        File.Delete(f.Resolve("keep.dll"));
        string again = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult repaired = Applier(f).Apply(InstallationFixture.StandardRequest(again, current: new Version(1, 1, 0)), null, CancellationToken.None);
        Recovery(f).Run();

        Assert.Equal(1, repaired.WrittenCount);
        Assert.Equal(0, repaired.DeletedCount);
        Assert.Equal("same", f.Read("keep.dll"));
        AssertConverged(f, "self-heal");
    }

    [Fact]
    public void Rollback_failure_left_behind_by_the_applier_is_repaired_by_startup_recovery()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.Any(
                FaultInjectingFileOperations.FailOnce("Move", "app.exe.sunew"),
                FaultInjectingFileOperations.FailAlways("Move", "old.dll.suold")),
        };
        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));
        Assert.Equal(UpdateStage.Rollback, ex.Stage);

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Empty(result.Errors);
        AssertConverged(f, "rollback retry");
        Assert.Equal("exe-v1", f.Read("app.exe"));
    }

    [Fact]
    public void Post_install_mismatch_is_rolled_back_and_the_reason_is_in_the_log_tail()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var fs = new FaultInjectingFileOperations(Physical)
        {
            OnOperation = op =>
            {
                if (op.Kind == "OpenRead" && op.Path.EndsWith("app.exe", StringComparison.Ordinal))
                {
                    File.WriteAllText(op.Path, "tampered");
                }
            },
        };
        string tail;

        using (var logger = new FileLogger(f.Layout.LogDirectory, null, UpdateLogLevel.Debug, Clock))
        {
            var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs, logger).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));
            Assert.Equal(UpdateStage.Verify, ex.Stage);
            tail = LogTail.Read(logger.CurrentFilePath);
        }

        Assert.Contains("安装后校验不符", tail, StringComparison.Ordinal);
        Assert.Contains("app.exe", tail, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(tail) <= LogTail.DefaultMaxBytes);
        Assert.Equal(RecoveryAction.None, Recovery(f).Run().Action);   // 应用器已自行回滚并删 journal
        AssertConverged(f, "post-install mismatch");
        Assert.Equal("exe-v1", f.Read("app.exe"));
    }

    [Fact]
    public void Unsafe_package_leaves_no_trace_anywhere_under_the_temp_root()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.StagePackage(new TestPackage { Version = new Version(1, 1, 0) }.Add("app.exe", "exe-v2").Add("../../evil.dll", "evil"));
        string zip = f.Layout.GetDownloadPath(new Version(1, 1, 0));
        string before = SnapshotOfEverything(f);

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Equal(before, SnapshotOfEverything(f));
        Assert.Equal(RecoveryAction.None, Recovery(f).Run().Action);
    }

    [Fact]
    public void Preserve_files_survive_two_updates_even_when_dropped_from_the_manifest()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string first = f.StagePackage(InstallationFixture.StandardV110());
        Applier(f).Apply(InstallationFixture.StandardRequest(first), null, CancellationToken.None);
        Recovery(f).Run();
        Assert.Equal("user", f.Read("settings.json"));

        TestPackage v120 = new TestPackage { Version = new Version(1, 2, 0) }
            .Add("app.exe", "exe-v3")
            .Add("keep.dll", "same")
            .Add("new.dll", "new")
            .Add("extra.cfg", "factory", FilePolicy.Preserve);   // settings.json 不再列出
        string second = f.StagePackage(v120);

        ApplyResult result = Applier(f).Apply(InstallationFixture.StandardRequest(second, current: new Version(1, 1, 0)), null, CancellationToken.None);
        Recovery(f).Run();

        Assert.Equal("user", f.Read("settings.json"));        // 新清单不再列出的用户文件也不删除
        Assert.Equal("factory", f.Read("extra.cfg"));         // 目标不存在的 preserve 文件被写入
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal("exe-v3", f.Read("app.exe"));
        Assert.Equal(new Version(1, 2, 0), f.ReadState().CurrentVersion);
        Assert.Empty(f.Leftovers());
    }

    [Fact]
    public void Disk_space_check_is_computed_from_the_package_and_its_manifest()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        long zipBytes = new FileInfo(zip).Length;

        using PackageContents package = PackageReader.Open(zip);
        long required = zipBytes + DiskSpaceChecker.ComputeInstallBytes(package.TotalFileBytes);
        DiskSpaceCheckResult enough = DiskSpaceChecker.Check(zipBytes, package.TotalFileBytes, f.Layout.InstallDirectory, f.Layout.DownloadCacheDirectory, _ => required);
        DiskSpaceCheckResult short1 = DiskSpaceChecker.Check(zipBytes, package.TotalFileBytes, f.Layout.InstallDirectory, f.Layout.DownloadCacheDirectory, _ => required - 1);

        Assert.Equal(6 + 4 + 3 + 7, package.TotalFileBytes);   // exe-v2 / same / new / default
        Assert.Equal(required, enough.RequiredBytes);
        Assert.True(enough.IsSufficient);
        Assert.False(short1.IsSufficient);
        Assert.True(DiskSpaceChecker.GetAvailableBytes(f.Layout.InstallDirectory) > 0);
    }
}
