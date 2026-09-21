using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class PackageApplierTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero));

    private static PackageApplier Applier(InstallationFixture f, IFileOperations? fs = null, IUpdateLog? log = null)
        => new(f.Layout, fs ?? Physical, log ?? new RecordingLog(), Clock);

    private sealed class ListProgress : IProgress<ApplyProgress>
    {
        public List<ApplyProgress> Reports { get; } = [];

        public Action<ApplyProgress>? OnReport { get; init; }

        public void Report(ApplyProgress value)
        {
            Reports.Add(value);
            OnReport?.Invoke(value);
        }
    }

    private static string Describe(FileOperation op)
    {
        string name = Path.GetFileName(op.Path);
        return op.Kind == "Move" ? $"Move {name} -> {Path.GetFileName(op.SecondPath)}" : $"{op.Kind} {name}";
    }

    [Fact]
    public void Applies_a_normal_update_and_leaves_backups_and_a_done_journal_for_startup()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult result = Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(new Version(1, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        Assert.Equal(2, result.WrittenCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        f.AssertStandardV110Files();
        Assert.Equal("exe-v1", f.Read("app.exe.suold"));
        Assert.Equal("old", f.Read("old.dll.suold"));
        Assert.Equal(new[] { ".smartupdater/manifest.json.suold", "app.exe.suold", "old.dll.suold" }, f.Leftovers());
        Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
        JournalReadResult journal = f.ReadJournal();
        Assert.Equal(JournalState.Done, journal.Journal!.State);
        Assert.Equal(new Version(1, 0, 0), journal.Journal.FromVersion);
        Assert.Single(f.CacheFiles());   // 下载缓存留给 done 恢复清理
    }

    [Fact]
    public void Disk_operations_follow_the_documented_order_deletes_writes_manifest_then_main_executable()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var fs = new FaultInjectingFileOperations(Physical);

        Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        string[] journalWrite = ["Delete journal.json.tmp", "OpenWrite journal.json.tmp", "FlushToDisk journal.json.tmp", "Move journal.json.tmp -> journal.json"];
        string[] expected =
        [
            .. journalWrite,
            "Delete new.dll.sunew", "OpenWrite new.dll.sunew", "FlushToDisk new.dll.sunew",
            "Delete manifest.json.sunew", "OpenWrite manifest.json.sunew", "FlushToDisk manifest.json.sunew",
            "Delete app.exe.sunew", "OpenWrite app.exe.sunew", "FlushToDisk app.exe.sunew",
            .. journalWrite,
            "Move old.dll -> old.dll.suold",
            "Move new.dll.sunew -> new.dll",
            "Move manifest.json -> manifest.json.suold", "Move manifest.json.sunew -> manifest.json",
            "Move app.exe -> app.exe.suold", "Move app.exe.sunew -> app.exe",
            .. journalWrite,
            "Delete state.json.tmp", "OpenWrite state.json.tmp", "FlushToDisk state.json.tmp", "Move state.json.tmp -> state.json",
        ];
        Assert.Equal(expected, fs.MutatingOperations.Select(Describe));
    }

    [Fact]
    public void Progress_is_reported_per_manifest_file_for_staging_and_verification()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var progress = new ListProgress();

        Applier(f).Apply(InstallationFixture.StandardRequest(zip), progress, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                new ApplyProgress(UpdateStage.Commit, 1, 2, 3, 9),
                new ApplyProgress(UpdateStage.Commit, 2, 2, 9, 9),
                new ApplyProgress(UpdateStage.Verify, 1, 2, 3, 9),
                new ApplyProgress(UpdateStage.Verify, 2, 2, 9, 9),
            },
            progress.Reports);
    }

    [Fact]
    public void First_install_writes_everything_including_preserve_files_and_records_no_from_version()
    {
        using var f = new InstallationFixture();
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult result = Applier(f).Apply(new ApplyRequest(zip, null, "app.exe"), null, CancellationToken.None);

        Assert.Null(result.FromVersion);
        Assert.Equal(4, result.WrittenCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal("default", f.Read("settings.json"));
        Assert.Equal("exe-v2", f.Read("app.exe"));
        Assert.Equal(new Version(1, 1, 0), f.ReadManifest()!.Version);
        Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
        Assert.Empty(f.Leftovers());                      // 首次安装没有任何 .suold
        Assert.Null(f.ReadJournal().Journal!.FromVersion);
    }

    [Fact]
    public void File_with_matching_hash_but_missing_on_disk_is_written_again()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        File.Delete(f.Resolve("keep.dll"));
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult result = Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(3, result.WrittenCount);
        Assert.Equal("same", f.Read("keep.dll"));
    }

    [Fact]
    public void Preserve_file_present_is_kept_and_reported_as_skipped()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var log = new RecordingLog();

        ApplyResult result = Applier(f, log: log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal("user", f.Read("settings.json"));
        Assert.Equal(1, result.SkippedCount);
        Assert.True(log.Contains("skip settings.json"));
    }

    [Fact]
    public void Preserve_file_dropped_from_the_new_manifest_is_never_deleted()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithInstalledFiles(
            new Version(1, 0, 0),
            ("app.exe", "exe-v1", FilePolicy.Replace),
            ("keep.dll", "same", FilePolicy.Replace),
            ("old.dll", "old", FilePolicy.Replace),
            ("settings.json", "user", FilePolicy.Preserve),
            ("user.cfg", "mine", FilePolicy.Preserve));
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult result = Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal("mine", f.Read("user.cfg"));
        Assert.False(f.Exists("user.cfg.suold"));
    }

    [Fact]
    public void Pending_file_whose_hash_differs_from_the_manifest_aborts_before_commit_and_leaves_no_trace()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        TestPackage package = InstallationFixture.StandardV110();
        string tamperedManifest = JsonSerializer.Serialize(package.BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest)
            .Replace(TestPackage.Sha256Hex("new"), TestPackage.Sha256Hex("tampered"), StringComparison.Ordinal);
        string zip = package.Save(f.Layout.GetDownloadPath(package.Version), manifestJsonOverride: tamperedManifest);
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("new.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Commit_failure_is_rolled_back_and_reported_as_Commit_stage()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailOnce("Move", "app.exe.sunew"),
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Empty(ex.RollbackFailures);
        Assert.IsType<SwapFailedException>(ex.InnerException);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        f.AssertStandardV1();
    }

    [Fact]
    public void Rollback_failure_keeps_the_journal_and_reports_Rollback_stage()
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
        Assert.Single(ex.RollbackFailures);
        Assert.Equal(JournalState.Committing, f.ReadJournal().Journal!.State);   // 留给启动恢复重试（由恢复测试验证能修好）
        Assert.Equal("exe-v1", f.Read("app.exe"));                                // 其余文件已回滚
        Assert.False(f.Exists("old.dll"));
        Assert.Equal("old", f.Read("old.dll.suold"));
        Assert.Equal(new Version(1, 0, 0), f.ReadState().CurrentVersion);
    }

    [Fact]
    public void Post_install_hash_mismatch_rolls_back_and_reports_Verify_stage()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            // 提交完成后、校验读取 app.exe 之前，把它改掉——模拟杀软 / 外部改写
            OnOperation = op =>
            {
                if (op.Kind == "OpenRead" && op.Path.EndsWith("app.exe", StringComparison.Ordinal))
                {
                    File.WriteAllText(op.Path, "tampered");
                }
            },
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("app.exe", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Unsafe_manifest_path_is_rejected_with_zero_side_effects()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(new TestPackage { Version = new Version(1, 1, 0) }.Add("app.exe", "exe-v2").Add("../evil.dll", "evil"));
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.IsType<PackageFormatException>(ex.InnerException);
        Assert.Equal(before, f.Snapshot());
        Assert.False(File.Exists(f.Temp.Resolve("evil.dll")));
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Refuses_to_start_while_a_journal_exists()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        new JournalStore(f.Layout.JournalFile, Physical, NullUpdateLog.Instance)
            .Write(new UpdateJournal { State = JournalState.Preparing, ToVersion = new Version(1, 1, 0) });
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<InvalidOperationException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Contains("恢复", ex.Message, StringComparison.Ordinal);   // 区分于 JournalStore.Write 的转移校验异常：必须在做任何事之前就拒绝
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Corrupt_local_manifest_is_treated_as_first_install_and_deletes_nothing()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        File.WriteAllText(f.Layout.ManifestFile, "{ garbage");
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var log = new RecordingLog();

        ApplyResult result = Applier(f, log: log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(3, result.WrittenCount);                 // app.exe、keep.dll、new.dll；settings.json 因 preserve 且存在而跳过
        Assert.Equal("old", f.Read("old.dll"));               // 差集算不出来就不删
        Assert.Equal("user", f.Read("settings.json"));
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Warning));
    }

    [Fact]
    public void Cancellation_during_staging_cleans_up_and_changes_nothing()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        using var cts = new CancellationTokenSource();
        var progress = new ListProgress { OnReport = _ => cts.Cancel() };

        Assert.Throws<OperationCanceledException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), progress, cts.Token));

        Assert.Single(progress.Reports);
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Unchanged_main_executable_is_not_written_and_the_manifest_is_the_last_swap()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        TestPackage package = new TestPackage { Version = new Version(1, 1, 0) }
            .Add("app.exe", "exe-v1")
            .Add("keep.dll", "same")
            .Add("new.dll", "new")
            .Add("settings.json", "default", FilePolicy.Preserve);
        string zip = f.StagePackage(package);
        var fs = new FaultInjectingFileOperations(Physical);

        ApplyResult result = Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(
            new[] { "new.dll.sunew", "manifest.json.sunew" },
            fs.MutatingOperations.Where(o => o.Kind == "Move" && o.Path.EndsWith(SwapFileNames.NewSuffix, StringComparison.Ordinal)).Select(o => Path.GetFileName(o.Path)));
        Assert.False(f.Exists("app.exe.suold"));
    }

    [Fact]
    public void Decisions_and_journal_transitions_are_logged()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var log = new RecordingLog();

        Applier(f, log: log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.True(log.Contains("write app.exe"));
        Assert.True(log.Contains("write new.dll"));
        Assert.True(log.Contains("delete old.dll"));
        Assert.True(log.Contains("skip settings.json"));
        Assert.True(log.Contains("(无) → preparing"));
        Assert.True(log.Contains("preparing → committing"));
        Assert.True(log.Contains("committing → done"));
    }

    // ───────────────────────── 失败路径完备性测试 ─────────────────────────

    // 开始新更新前清掉上一次更新遗留的 *.suold 与孤儿 *.sunew。
    // journal 不存在 ⇒ 上一次更新已收尾（先删 .suold 与缓存，最后才删 journal），
    // 或从未产生过 .suold；journal 存在时步骤 0 直接拒绝、什么都不动（恢复需要那些 .suold）。所以此时仍在的 .suold 只可能是陈旧数据。
    [Fact]
    public void Stale_backups_and_orphan_pending_files_are_removed_before_the_update_and_never_copied_back()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "ancient-exe")                       // 会被本次更新替换的文件的陈旧备份
         .WithFile("keep.dll.suold", "ancient-keep")                     // 本次不动的文件的陈旧备份
         .WithFile(".smartupdater/manifest.json.suold", "ancient-manifest")
         .WithFile("sub/orphan.dll.sunew", "half")                       // 子目录里的孤儿 .sunew
         .WithFile("app.exe.sunew", "half-exe");                         // 将被本次更新写的目标的孤儿 .sunew
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var log = new RecordingLog();

        ApplyResult result = Applier(f, log: log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(2, result.WrittenCount);
        f.AssertStandardV110Files();
        Assert.Equal("exe-v1", f.Read("app.exe.suold"));                 // 是本次更新替换掉的 v1，不是陈旧的 "ancient-exe"
        Assert.Equal(new[] { ".smartupdater/manifest.json.suold", "app.exe.suold", "old.dll.suold" }, f.Leftovers());
        Assert.NotEqual("ancient-manifest", File.ReadAllText(f.Layout.ManifestFile + SwapFileNames.OldSuffix));
        Assert.True(log.Contains("清理上次更新遗留"));
    }

    [Fact]
    public void Stale_backup_never_overwrites_the_current_file_when_the_update_fails_and_rolls_back()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "ancient-exe")
         .WithFile("keep.dll.suold", "ancient-keep");
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailOnce("Move", "new.dll.sunew"),   // 主 exe 与清单的提交都还没开始
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        f.AssertStandardV1();
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Stale_backup_that_cannot_be_removed_fails_the_update_before_anything_is_touched()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "ancient-exe");
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailAlways("Delete", "app.exe.suold"),
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs, log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Contains("遗留", ex.Message, StringComparison.Ordinal);
        Assert.Contains("app.exe.suold", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.Snapshot());                                     // 连陈旧文件都没动，更没进入 preparing
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("app.exe.suold", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_files_are_left_alone_when_a_journal_exists_because_recovery_needs_them()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "the-real-backup");
        new JournalStore(f.Layout.JournalFile, Physical, NullUpdateLog.Instance)
            .Write(new UpdateJournal { State = JournalState.Preparing, ToVersion = new Version(1, 1, 0) });
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();

        Assert.Throws<InvalidOperationException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(before, f.Snapshot());
    }

    // 步骤 0 的闸门也覆盖损坏的 journal：它同样是"有未完成的事务"，恢复可能需要磁盘上的 .suold，清理不能先于闸门。
    // 若闸门只放行 Valid 以外的情形，清理会先删掉陈旧文件，之后才因 journal 损坏在写 journal 时抛出（那条消息也含"恢复"），
    // 所以除了异常类型 / 消息，还必须断言快照不变（陈旧文件没被清）。
    [Fact]
    public void Corrupt_journal_is_refused_at_step_zero_and_stale_files_are_not_swept()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "maybe-the-real-backup");
        File.WriteAllText(f.Layout.JournalFile, "{ garbage");
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<InvalidOperationException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Contains("未完成的更新事务", ex.Message, StringComparison.Ordinal);   // 步骤 0 自己的消息，不是 JournalStore.Write 的
        Assert.Contains("恢复", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Corrupt, f.ReadJournal().Status);
    }

    // 连枚举本身都失败（例如某个子目录 ACL 拒绝访问，Directory.EnumerateFiles 的一次异常会中止整个扫描，
    // 连可访问目录里的陈旧 .suold 也看不到）时，清理陈旧文件的前提无法确认，必须与"清不掉"一样失败，不能带着未验证的前提进入 preparing。
    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    public void Failure_to_enumerate_the_install_directory_fails_the_update_before_anything_is_touched(string cause)
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.suold", "ancient-exe");                          // 扫描看不见的陈旧备份：这正是危险所在
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = op => op.Kind == "EnumerateFiles"
                ? cause == "io" ? new IOException("模拟枚举失败") : new UnauthorizedAccessException("模拟子目录拒绝访问")
                : null,
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs, log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Empty(ex.RollbackFailures);
        Assert.Contains("无法枚举", ex.Message, StringComparison.Ordinal);
        Assert.Contains(f.Layout.InstallDirectory, ex.Message, StringComparison.Ordinal);   // 点名目录
        Assert.NotNull(ex.InnerException);                                                   // 保留原因
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        AssertNothingCommitted(fs);
        Assert.DoesNotContain(fs.MutatingOperations, o => o.Kind == "OpenWrite");           // 连 journal 都没开始写
        Assert.Contains(log.Entries, e => e.Level >= UpdateLogLevel.Warning && e.Message.Contains("枚举", StringComparison.Ordinal));
    }

    // 撕裂写：崩溃后最常见的残留是半截或零长度的 .sunew；引擎不得把".sunew 存在"当成"已完整暂存"。
    [Fact]
    public void Truncated_or_empty_pending_files_left_by_a_crash_are_never_trusted()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithBytes("new.dll.sunew", [0x6E])                                              // 截断：只剩 1 字节
         .WithBytes("app.exe.sunew", [])                                                  // 零长度
         .WithBytes(".smartupdater/manifest.json.sunew", Encoding.UTF8.GetBytes("{\"schemaV"));   // 半截 JSON
        string zip = f.StagePackage(InstallationFixture.StandardV110());

        ApplyResult result = Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(2, result.WrittenCount);
        f.AssertStandardV110Files();                                                       // 内容来自 zip，不是残留
        Assert.Equal(new Version(1, 1, 0), f.ReadManifest()!.Version);
        Assert.DoesNotContain(f.Leftovers(), p => p.EndsWith(SwapFileNames.NewSuffix, StringComparison.Ordinal));
    }

    // 暂存阶段的 hash 校验是在"读 zip 条目"时算的，验证的是包内容与清单一致；它看不到存储层把已写的数据弄丢了。
    // 落盘内容被撕裂（截断 / 补零：NTFS 断电后最常见的两种形态）只有安装后的逐文件校验能发现，并由它回滚——这是最后一道防线。
    [Theory]
    [InlineData("truncate")]
    [InlineData("zerofill")]
    public void A_pending_file_whose_on_disk_content_was_torn_is_caught_by_the_post_install_check_and_rolled_back(string mode)
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var shaping = new WriteShapingFileOperations(Physical)
        {
            Shape = (path, chunk) => path.EndsWith("new.dll.sunew", StringComparison.Ordinal)
                ? mode == "truncate" ? chunk[..1] : new byte[chunk.Length]
                : chunk,
        };
        var fs = new FaultInjectingFileOperations(shaping);

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("new.dll", ex.Message, StringComparison.Ordinal);
        Assert.Contains("安装后校验", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ex.RollbackFailures);
        Assert.Equal(before, f.Snapshot());                                     // 撕裂的文件没有留在目标位置，旧版本完整回来
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        f.AssertStandardV1();
    }

    // 与上一条的分工：这里是 zip 里的内容本身对不上清单（暂存阶段发现，提交前中止），必须一个改名都没发生过。
    // 若暂存阶段没有 hash 校验，问题会拖到提交之后由安装后校验回滚——结果看起来一样（磁盘还是旧版本），所以要断言"没有改名"。
    [Fact]
    public void Zip_content_that_does_not_match_the_manifest_hash_is_rejected_before_any_rename()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        TestPackage package = InstallationFixture.StandardV110();
        string tamperedManifest = JsonSerializer.Serialize(package.BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest)
            .Replace(TestPackage.Sha256Hex("exe-v2"), TestPackage.Sha256Hex("something else"), StringComparison.Ordinal);
        string zip = package.Save(f.Layout.GetDownloadPath(package.Version), manifestJsonOverride: tamperedManifest);
        IReadOnlyList<string> before = f.Snapshot();
        var fs = new FaultInjectingFileOperations(Physical);

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("待提交文件 app.exe", ex.Message, StringComparison.Ordinal);
        AssertNothingCommitted(fs);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    // stored 条目可以谎报大小。必须用 NoCompression 构造；deflate 条目在没有任何上限代码时也会被 BCL 截断，是假信号。
    [Fact]
    public void Stored_entry_that_lies_about_its_size_is_cut_off_at_the_declared_size_and_fails()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        const string Declared = "0123456789abcdef";                                        // 16 字节
        byte[] payload = new byte[1024 * 1024];
        Array.Fill(payload, (byte)'x');
        TestPackage package = new TestPackage { Version = new Version(1, 1, 0) }
            .Add("app.exe", "exe-v2")
            .Add("new.dll", Declared)
            .Add("settings.json", "default", FilePolicy.Preserve);
        string zip = package.Save(
            f.Layout.GetDownloadPath(package.Version),
            archive =>
            {
                archive.GetEntry("new.dll")!.Delete();
                using Stream entry = archive.CreateEntry("new.dll", System.IO.Compression.CompressionLevel.NoCompression).Open();
                entry.Write(payload);
            });
        LieAboutUncompressedSize(zip, "new.dll", Declared.Length);

        // 前置：这个包确实是"声明 16、实际 1 MiB"，并且通过了 PackageReader 的头部检查——否则本测试没有验证力。
        using (PackageContents raw = PackageReader.Open(zip))
        using (Stream rawEntry = raw.OpenEntry("new.dll"))
        {
            Assert.True(CountBytes(rawEntry) > Declared.Length, "构造的 stored 条目没有吐出超出声明的字节，测试无效。");
        }

        IReadOnlyList<string> before = f.Snapshot();
        var shaping = new WriteShapingFileOperations(Physical);
        var fs = new FaultInjectingFileOperations(shaping);

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("new.dll", ex.Message, StringComparison.Ordinal);
        Assert.Contains("超出", ex.Message, StringComparison.Ordinal);
        long written = shaping.BytesWritten.GetValueOrDefault("new.dll.sunew");   // 多出的那一个字节不落盘，所以可以是 0
        Assert.True(written <= Declared.Length, $"超过声明大小后仍继续写盘：写了 {written} 字节。");
        AssertNothingCommitted(fs);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Corrupted_entry_data_fails_as_Verify_and_leaves_no_trace()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        TestPackage package = new TestPackage { Version = new Version(1, 1, 0) }
            .Add("app.exe", "exe-v2")
            .Add("new.dll", string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 200)));
        string zip = f.StagePackage(package);
        CorruptEntryPayload(zip, "new.dll");
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.IsType<InvalidDataException>(ex.InnerException);                  // BCL 解压时发现数据损坏，归为 Verify 而不是磁盘写入失败
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    // 文件名与目录名撞车（清单里同时有 a 与 a/b.txt）。包读取阶段不拒绝这种包，由应用器干净地失败。
    [Fact]
    public void Manifest_with_a_file_and_a_directory_of_the_same_name_fails_with_zero_side_effects()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(new TestPackage { Version = new Version(1, 1, 0) }
            .Add("app.exe", "exe-v2")
            .Add("A", "file")
            .Add("a/b.txt", "nested"));
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.Contains("a/b.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.Snapshot());
        Assert.False(Directory.Exists(f.Resolve("a")));                          // 没有留下半截目录
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Directory_already_on_disk_where_a_manifest_file_belongs_is_rolled_back_cleanly()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("data/inner.txt", "user data");                              // 磁盘上 data 是目录
        string zip = f.StagePackage(new TestPackage { Version = new Version(1, 1, 0) }
            .Add("app.exe", "exe-v2")
            .Add("data", "i am a file now"));
        IReadOnlyList<string> before = f.Snapshot();

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Empty(ex.RollbackFailures);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal("user data", f.Read("data/inner.txt"));
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        f.AssertStandardV1();
    }

    // 失败路径完备性：Apply 的契约是"失败一律抛 UpdateFailedException"，自己做的 IO 失败不能漏出原始 IOException。
    [Fact]
    public void Unreadable_local_manifest_fails_the_update_instead_of_guessing()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenRead", "manifest.json"),
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Theory]
    [InlineData(1)]   // preparing：还没动任何东西
    [InlineData(2)]   // committing：.sunew 已落盘，改名还没开始
    [InlineData(3)]   // done：改名与安装后校验都已完成，只差标记 → 撤回，回到旧版本
    public void Journal_write_failure_at_any_phase_leaves_the_old_version_and_no_journal(int failingWrite)
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        int seen = 0;
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = op => op.Kind == "OpenWrite" && op.Path.EndsWith("journal.json.tmp", StringComparison.Ordinal) && ++seen == failingWrite
                ? new IOException("模拟 journal 写入失败")
                : null,
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Empty(ex.RollbackFailures);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        f.AssertStandardV1();
    }

    // JournalStore.Write 在磁盘上的 journal 被外部改坏 / 状态转移非法时抛 InvalidOperationException（不是 IO 异常）。
    // 它必须与 IO 失败同样被收敛成带 Stage 的 UpdateFailedException，并回到确定的旧版本。
    [Theory]
    [InlineData(1)]   // preparing：步骤 0 之后、写第一份 journal 之前，磁盘上冒出一份损坏的 journal（别人的事务，不能动它）
    [InlineData(2)]   // committing：.sunew 已落盘，journal 被改坏
    [InlineData(3)]   // done：改名与安装后校验都已完成，journal 被改坏 → 撤回
    public void Journal_write_rejected_with_InvalidOperationException_at_any_phase_is_converted_and_rolled_back(int failingWrite)
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        int journalMoves = 0;
        bool tamperOnNextOperation = false;
        bool tampered = false;
        var fs = new FaultInjectingFileOperations(Physical);
        fs.OnOperation = op =>
        {
            if (tampered)
            {
                return;
            }

            if (failingWrite == 1)
            {
                if (op.Kind == "EnumerateFiles")
                {
                    tampered = true;
                    File.WriteAllText(f.Layout.JournalFile, "{ garbage");
                }

                return;
            }

            if (tamperOnNextOperation)
            {
                tampered = true;
                File.WriteAllText(f.Layout.JournalFile, "{ garbage");
                return;
            }

            if (op.Kind == "Move" && op.Path.EndsWith("journal.json.tmp", StringComparison.Ordinal) && ++journalMoves == failingWrite - 1)
            {
                tamperOnNextOperation = true;   // 这次 Move 之后的第一个操作里改坏 journal
            }
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, TestContext.Current.CancellationToken));

        Assert.True(tampered);
        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Empty(ex.RollbackFailures);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        f.AssertStandardV1();
        Assert.Empty(Directory.EnumerateFiles(f.Layout.InstallDirectory, "*.sunew", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(f.Layout.InstallDirectory, "*.suold", SearchOption.AllDirectories));

        if (failingWrite == 1)
        {
            Assert.Equal(JournalStatus.Corrupt, f.ReadJournal().Status);   // 不属于本次事务的 journal 原样保留给启动恢复
        }
        else
        {
            Assert.Equal(before, f.Snapshot());
            Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        }
    }

    [Fact]
    public void Pending_file_that_vanishes_before_commit_fails_cleanly_without_touching_any_target()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        IReadOnlyList<string> before = f.Snapshot();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            // FileSwapper.Commit 的预检读 .sunew 是否存在：在它看之前，被杀软隔离 / 外部清理
            OnOperation = op =>
            {
                if (op.Kind == "FileExists" && op.Path.EndsWith("new.dll.sunew", StringComparison.Ordinal))
                {
                    File.Delete(op.Path);
                }
            },
        };

        var ex = Assert.Throws<UpdateFailedException>(() => Applier(f, fs).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Commit, ex.Stage);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void State_save_failure_after_done_is_only_a_warning_and_recovery_can_fill_it_in()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        string zip = f.StagePackage(InstallationFixture.StandardV110());
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenWrite", "state.json.tmp"),
        };

        ApplyResult result = Applier(f, fs, log).Apply(InstallationFixture.StandardRequest(zip), null, CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        Assert.Equal(JournalState.Done, f.ReadJournal().Journal!.State);
        Assert.Equal(new Version(1, 0, 0), f.ReadState().CurrentVersion);        // 没写成；done 恢复会补齐
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Exception is IOException);
        f.AssertStandardV110Files();
    }

    // ───────────────────────── 测试内的小工具 ─────────────────────────

    /// <summary>
    /// 只改 zip 中央目录与本地文件头里的 uncompressedSize（不动 compressedSize）：stored 条目的解压流受 compressedSize 约束，
    /// 于是 <c>Entry.Length</c> 声明很小、实际吐出很大——PackageReader 的头部检查看不出来。
    /// </summary>
    private static void LieAboutUncompressedSize(string zipPath, string entryName, int declared)
    {
        byte[] zip = File.ReadAllBytes(zipPath);
        (int central, int local, _) = LocateEntry(zip, entryName);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 6)) & 0x0008);   // 本地头里有真实大小（没有数据描述符）
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), (uint)declared);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22), (uint)declared);
        File.WriteAllBytes(zipPath, zip);
    }

    /// <summary>断言没有任何改名发生过——journal 自己的 .tmp → 正式文件除外。即：失败发生在提交之前，没有碰任何 .sunew 之外的东西。</summary>
    private static void AssertNothingCommitted(FaultInjectingFileOperations fs)
        => Assert.DoesNotContain(fs.MutatingOperations, o => o.Kind == "Move" && !o.Path.EndsWith("journal.json.tmp", StringComparison.Ordinal));

    /// <summary>读到流结束，返回总字节数。</summary>
    private static long CountBytes(Stream stream)
    {
        long total = 0;
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }

        return total;
    }

    /// <summary>翻转条目 deflate 数据开头的若干字节（块头 / 霍夫曼表），zip 头部与中央目录保持完好（PackageReader.Open 仍然接受）。</summary>
    private static void CorruptEntryPayload(string zipPath, string entryName)
    {
        byte[] zip = File.ReadAllBytes(zipPath);
        (_, _, int payload) = LocateEntry(zip, entryName);
        for (int i = 0; i < 8; i++)
        {
            zip[payload + i] ^= 0xFF;
        }

        File.WriteAllBytes(zipPath, zip);
    }

    private static (int Central, int Local, int Payload) LocateEntry(byte[] zip, string entryName)
    {
        int eocd = zip.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        Assert.True(eocd >= 0, "找不到 zip 的 end-of-central-directory。");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        int position = BinaryPrimitives.ReadInt32LittleEndian(zip.AsSpan(eocd + 16));

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position)));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 32));
            int local = BinaryPrimitives.ReadInt32LittleEndian(zip.AsSpan(position + 42));

            if (Encoding.UTF8.GetString(zip, position + 46, nameLength) == entryName)
            {
                int localName = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 26));
                int localExtra = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 28));
                return (position, local, local + 30 + localName + localExtra);
            }

            position += 46 + nameLength + extraLength + commentLength;
        }

        throw new InvalidOperationException($"zip 里没有条目 {entryName}。");
    }

    /// <summary>
    /// 包住真实文件系统：记录每个被写文件实际收到的字节数（<see cref="BytesWritten"/>，键为文件名），
    /// 并可用 <see cref="Shape"/> 把写入的每一块改成别的内容——截断 / 补零，模拟断电前只落盘了一部分的撕裂写。
    /// </summary>
    private sealed class WriteShapingFileOperations(IFileOperations inner) : IFileOperations
    {
        public Dictionary<string, long> BytesWritten { get; } = new(StringComparer.Ordinal);

        /// <summary>(完整路径, 一块数据) → 实际落盘的数据。为 null 时原样写入。</summary>
        public Func<string, byte[], byte[]>? Shape { get; init; }

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string sourcePath, string destinationPath, bool overwrite) => inner.Move(sourcePath, destinationPath, overwrite);

        public void Delete(string path) => inner.Delete(path);

        public Stream OpenWrite(string path) => new ShapedStream(inner.OpenWrite(path), path, this);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public void FlushToDisk(Stream stream) => inner.FlushToDisk(stream is ShapedStream shaped ? shaped.Inner : stream);

        public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern, bool recursive) => inner.EnumerateFiles(directory, searchPattern, recursive);

        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

        public void SetAttributes(string path, FileAttributes attributes) => inner.SetAttributes(path, attributes);

        public long GetFileLength(string path) => inner.GetFileLength(path);

        private sealed class ShapedStream(Stream stream, string path, WriteShapingFileOperations owner) : Stream
        {
            public Stream Inner => stream;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => stream.Length;

            public override long Position
            {
                get => stream.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => stream.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                string name = Path.GetFileName(path);
                owner.BytesWritten[name] = owner.BytesWritten.GetValueOrDefault(name) + buffer.Length;
                stream.Write(owner.Shape is null ? buffer : owner.Shape(path, buffer.ToArray()));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    stream.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
