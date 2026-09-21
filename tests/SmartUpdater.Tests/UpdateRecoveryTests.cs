using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateRecoveryTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;

    private static UpdateRecovery Recovery(InstallationFixture f, IFileOperations? fs = null, IUpdateLog? log = null)
        => new(f.Layout, fs ?? Physical, log ?? new RecordingLog());

    private static UpdateJournal StandardJournal(JournalState state) => new()
    {
        State = state,
        FromVersion = new Version(1, 0, 0),
        ToVersion = new Version(1, 1, 0),
        StartedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        Writes =
        [
            new JournalEntry { Path = "new.dll", HadTarget = false },
            new JournalEntry { Path = UpdateLayout.ManifestRelativePath, HadTarget = true },
            new JournalEntry { Path = "app.exe", HadTarget = true },
        ],
        Deletes = ["old.dll"],
    };

    private static void SeedJournal(InstallationFixture f, JournalState state)
    {
        var store = new JournalStore(f.Layout.JournalFile, Physical, NullUpdateLog.Instance);
        store.Write(StandardJournal(JournalState.Preparing));
        if (state >= JournalState.Committing)
        {
            store.Write(StandardJournal(JournalState.Committing));
        }

        if (state == JournalState.Done)
        {
            store.Write(StandardJournal(JournalState.Done));
        }
    }

    private static string NewManifestJson()
        => JsonSerializer.Serialize(InstallationFixture.StandardV110().BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest);

    /// <summary>崩溃于提交中途：old.dll 已改名、new.dll 已移入、manifest 已交换、app.exe 尚未动（.sunew 就位）。</summary>
    private static InstallationFixture HalfCommitted()
    {
        InstallationFixture f = InstallationFixture.StandardV1();
        File.Move(f.Resolve("old.dll"), f.Resolve("old.dll.suold"));
        File.WriteAllText(f.Resolve("new.dll"), "new");
        File.Move(f.Layout.ManifestFile, f.Layout.ManifestFile + SwapFileNames.OldSuffix);
        File.WriteAllText(f.Layout.ManifestFile, NewManifestJson());
        File.WriteAllText(f.Resolve("app.exe.sunew"), "exe-v2");
        SeedJournal(f, JournalState.Committing);
        return f;
    }

    /// <summary>全部改名完成、journal 仍是 committing（崩溃于 done 之前）。</summary>
    private static InstallationFixture FullyMovedButNotDone(JournalState journalState = JournalState.Committing)
    {
        InstallationFixture f = InstallationFixture.StandardV1();
        File.Move(f.Resolve("old.dll"), f.Resolve("old.dll.suold"));
        File.WriteAllText(f.Resolve("new.dll"), "new");
        File.Move(f.Layout.ManifestFile, f.Layout.ManifestFile + SwapFileNames.OldSuffix);
        File.WriteAllText(f.Layout.ManifestFile, NewManifestJson());
        File.Move(f.Resolve("app.exe"), f.Resolve("app.exe.suold"));
        File.WriteAllText(f.Resolve("app.exe"), "exe-v2");
        SeedJournal(f, journalState);
        return f;
    }

    private static void AssertCleanV1(InstallationFixture f)
    {
        f.AssertStandardV1();
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void No_journal_means_nothing_happens_even_with_stray_backups()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("stray.dll.suold", "stray");

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.None, result.Action);
        Assert.False(result.JustUpdated);
        Assert.Equal("stray", f.Read("stray.dll.suold"));
        f.AssertStandardV1();
    }

    [Fact]
    public void Preparing_discards_all_pending_files_and_the_journal()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithFile("app.exe.sunew", "exe-v2");
        f.WithFile("sub/deep/lib.dll.sunew", "x");
        File.WriteAllText(f.Layout.ManifestFile + SwapFileNames.NewSuffix, NewManifestJson());
        SeedJournal(f, JournalState.Preparing);

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.DiscardedPreparing, result.Action);
        Assert.Equal(new Version(1, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        Assert.Empty(result.Errors);
        AssertCleanV1(f);
    }

    [Fact]
    public void Committing_half_way_is_rolled_back_to_the_old_version()
    {
        using InstallationFixture f = HalfCommitted();
        var log = new RecordingLog();

        RecoveryResult result = Recovery(f, log: log).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Equal(new Version(1, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        Assert.Empty(result.Errors);
        AssertCleanV1(f);
        Assert.True(log.Contains("committing"));
    }

    [Fact]
    public void Committing_with_every_file_already_moved_still_rolls_back()
    {
        using InstallationFixture f = FullyMovedButNotDone();

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        AssertCleanV1(f);
    }

    [Fact]
    public void Committing_recovery_survives_a_crash_at_any_step_and_converges_on_rerun()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using InstallationFixture f = FullyMovedButNotDone();
            var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(n) };

            try
            {
                Recovery(f, fs).Run();
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            Recovery(f).Run();
            AssertCleanV1(f);
        }

        Assert.True(totalSteps >= 10, $"只扫描到 {totalSteps} 步");
    }

    [Fact]
    public void Committing_with_a_locked_backup_keeps_the_journal_and_succeeds_on_retry()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        RecoveryResult first;
        using (new FileStream(f.Resolve("old.dll.suold"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            first = Recovery(f).Run();
        }

        Assert.Equal(RecoveryAction.RolledBack, first.Action);
        Assert.NotEmpty(first.Errors);
        Assert.Equal(JournalState.Committing, f.ReadJournal().Journal!.State);   // 保留，等下次重试
        Assert.Equal("exe-v1", f.Read("app.exe"));                                // 其余文件已回滚
        Assert.False(f.Exists("new.dll"));

        RecoveryResult second = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, second.Action);
        Assert.Empty(second.Errors);
        AssertCleanV1(f);
    }

    [Fact]
    public void Done_fixes_state_deletes_backups_clears_cache_and_reports_just_updated()
    {
        using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
        File.WriteAllText(Path.Combine(f.Layout.DownloadCacheDirectory, "1.1.0.zip"), "zip");
        File.WriteAllText(Path.Combine(f.Layout.DownloadCacheDirectory, "stray.tmp"), "tmp");
        Assert.Equal(new Version(1, 0, 0), f.ReadState().CurrentVersion);   // 崩溃发生在写 state 之前

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.CompletedUpdate, result.Action);
        Assert.True(result.JustUpdated);
        Assert.Equal(new Version(1, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);
        Assert.Empty(result.Errors);
        f.AssertStandardV110Files();
        Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
        Assert.Empty(f.Leftovers());
        Assert.Empty(f.CacheFiles());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Done_is_idempotent_and_just_updated_fires_only_once()
    {
        using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);

        Assert.True(Recovery(f).Run().JustUpdated);
        RecoveryResult second = Recovery(f).Run();

        Assert.Equal(RecoveryAction.None, second.Action);
        Assert.False(second.JustUpdated);
        f.AssertStandardV110Files();
    }

    [Fact]
    public void Done_recovery_survives_a_crash_at_any_step_and_converges_on_rerun()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
            File.WriteAllText(Path.Combine(f.Layout.DownloadCacheDirectory, "1.1.0.zip"), "zip");
            var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(n) };

            try
            {
                Recovery(f, fs).Run();
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            RecoveryResult rerun = Recovery(f).Run();

            Assert.True(rerun.Action is RecoveryAction.CompletedUpdate or RecoveryAction.None, $"崩溃于第 {n} 步后重跑得到 {rerun.Action}");
            f.AssertStandardV110Files();
            Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
            Assert.Empty(f.Leftovers());
            Assert.Empty(f.CacheFiles());
            Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        }

        Assert.True(totalSteps >= 8, $"只扫描到 {totalSteps} 步");
    }

    [Fact]
    public void Done_writes_state_first_and_deletes_the_journal_last()
    {
        using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
        var fs = new FaultInjectingFileOperations(Physical);

        Recovery(f, fs).Run();

        IReadOnlyList<FileOperation> mutating = fs.MutatingOperations;
        Assert.EndsWith("state.json" + AtomicFile.TempSuffix, mutating[0].Path, StringComparison.Ordinal);
        Assert.Equal("Delete", mutating[^2].Kind);
        Assert.EndsWith("journal.json", mutating[^2].Path, StringComparison.Ordinal);
        Assert.EndsWith("journal.json" + AtomicFile.TempSuffix, mutating[^1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_with_an_undeletable_backup_still_completes_and_reports_the_error()
    {
        using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
        RecoveryResult result;
        using (new FileStream(f.Resolve("app.exe.suold"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Recovery(f).Run();
        }

        Assert.True(result.JustUpdated);
        Assert.Single(result.Errors);
        Assert.Contains("app.exe.suold", result.Errors[0], StringComparison.Ordinal);
        Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Equal(new[] { "app.exe.suold" }, f.Leftovers());               // 残留交给下一次 done / preparing 清理
        Assert.Equal(RecoveryAction.None, Recovery(f).Run().Action);
    }

    [Fact]
    public void Corrupt_journal_falls_back_to_restoring_every_backup()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        File.WriteAllText(f.Layout.JournalFile, "{ broken");

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Null(result.FromVersion);
        Assert.Null(result.ToVersion);
        Assert.Contains(result.Errors, e => e.Contains("journal 损坏", StringComparison.Ordinal));
        Assert.Equal("exe-v1", f.Read("app.exe"));
        Assert.Equal("old", f.Read("old.dll"));
        Assert.Equal(new Version(1, 0, 0), f.ReadManifest()!.Version);
        Assert.True(f.Exists("new.dll"));                                        // 无 .suold 的新增文件无法识别，留在原地（已记入 README）
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void An_integer_journal_state_is_corrupt_and_falls_back_to_generic_rollback()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        string tampered = File.ReadAllText(f.Layout.JournalFile).Replace("\"committing\"", "99", StringComparison.Ordinal);
        Assert.DoesNotContain("\"committing\"", tampered, StringComparison.Ordinal);   // 确实改到了那个字段
        File.WriteAllText(f.Layout.JournalFile, tampered);

        // CamelCaseEnumConverter 拒绝整数状态，state=99 在 JSON 层就是 Corrupt，
        // Run() 里 `_ =>` 的"无法识别"分支不可达（保留作纵深防御）；本测试验证走通用回滚。
        Assert.Equal(JournalStatus.Corrupt, f.ReadJournal().Status);

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Contains(result.Errors, e => e.Contains("journal 损坏", StringComparison.Ordinal));
        Assert.Equal("exe-v1", f.Read("app.exe"));
        Assert.Equal("old", f.Read("old.dll"));
        Assert.Equal(new Version(1, 0, 0), f.ReadManifest()!.Version);
        Assert.Empty(f.Leftovers());
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void RollbackFromBackups_is_the_manual_recovery_entry_and_ignores_the_journal()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        File.Delete(f.Layout.JournalFile);
        f.WithFile("app.exe.sunew", "leftover");

        RecoveryResult result = Recovery(f).RollbackFromBackups();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Empty(result.Errors);
        Assert.Equal("exe-v1", f.Read("app.exe"));
        Assert.Equal("old", f.Read("old.dll"));
        Assert.Equal(new Version(1, 0, 0), f.ReadManifest()!.Version);
        Assert.Empty(f.Leftovers());
    }

    [Fact]
    public void Generic_rollback_renames_the_new_file_aside_instead_of_deleting_it()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        File.Delete(f.Layout.JournalFile);
        var fs = new FaultInjectingFileOperations(Physical);

        Recovery(f, fs).RollbackFromBackups();

        string[] appExeOps = [.. fs.MutatingOperations
            .Where(o => Path.GetFileName(o.Path) is "app.exe" or "app.exe.sunew" or "app.exe.suold")
            .Select(o => o.Kind == "Move" ? $"Move {Path.GetFileName(o.Path)} -> {Path.GetFileName(o.SecondPath)}" : $"{o.Kind} {Path.GetFileName(o.Path)}")];
        Assert.Equal(
            new[] { "Move app.exe -> app.exe.sunew", "Move app.exe.suold -> app.exe", "Delete app.exe.sunew" },
            appExeOps);   // 正在运行的新 exe 能改名不能删：先挪开再移回，最后才尝试删 .sunew
    }

    [Fact]
    public void Repairs_are_logged()
    {
        using InstallationFixture f = HalfCommitted();
        var log = new RecordingLog();

        Recovery(f, log: log).Run();

        Assert.True(log.Contains("发现 journal"));
        Assert.Contains(log.Entries, e => e.Message.Contains("old.dll", StringComparison.Ordinal) && e.Stage == UpdateStage.Rollback);
        Assert.True(log.Contains("journal 已删除"));
    }

    // ---- 撕裂写的 .sunew 绝不能被当成"已完整暂存" ----
    // 崩溃注入器造不出撕裂写（seam 操作之间没有注入点，异常展开经过 using 时 FileStream.Dispose 会把缓冲刷进文件），
    // 所以这两个种子用 WithBytes 手工造截断与零长度的 .sunew。恢复引擎的防线是 journal 状态：
    // 三个分支都只删除 .sunew，从不把任何 .sunew 改名就位——内容完整与否根本不参与判断。

    [Fact]
    public void Preparing_discards_torn_and_empty_pending_files_without_installing_them()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        f.WithBytes("app.exe.sunew", [0x4D, 0x5A]);        // 截断：只写下了 MZ 头
        f.WithBytes("keep.dll.sunew", []);                 // 零长度
        f.WithBytes("new.dll.sunew", "ne"u8.ToArray());    // 半截的新增文件
        SeedJournal(f, JournalState.Preparing);

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.DiscardedPreparing, result.Action);
        Assert.Empty(result.Errors);
        AssertCleanV1(f);   // 三个残片都被删；app.exe / keep.dll 仍是 v1 的内容，new.dll 没有被造出来
    }

    [Fact]
    public void Committing_rollback_discards_a_torn_pending_file_instead_of_installing_it()
    {
        using InstallationFixture f = HalfCommitted();
        f.WithBytes("app.exe.sunew", [0x4D]);   // 崩溃于写 app.exe.sunew 的中途，只落了一个字节

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Empty(result.Errors);
        Assert.Equal("exe-v1", f.Read("app.exe"));   // 残片没有被改名就位
        AssertCleanV1(f);
    }

    // ---- 恢复只看磁盘，上一次更新遗留的陈旧 .suold 不得覆盖当前文件 ----

    [Fact]
    public void Committing_rollback_keeps_the_current_file_when_a_stale_backup_is_present()
    {
        using InstallationFixture f = HalfCommitted();
        f.WithFile("app.exe.suold", "exe-v0");   // 上一次更新遗留的陈旧备份，比当前的 exe-v1 还旧

        RecoveryResult result = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.Empty(result.Errors);
        Assert.True(f.Exists("app.exe"));               // 当前文件既没被删
        Assert.Equal("exe-v1", f.Read("app.exe"));      // 也没被 exe-v0 盖回去：app.exe 与 app.exe.sunew 同在 = 这一步尚未开始
        Assert.Equal("old", f.Read("old.dll"));
        Assert.Equal(new Version(1, 0, 0), f.ReadManifest()!.Version);
        Assert.False(f.Exists("new.dll"));
        Assert.Equal(new[] { "app.exe.suold" }, f.Leftovers());   // 陈旧备份留给下一次 done / preparing 清理
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
    }

    [Fact]
    public void Done_recovery_with_a_stale_backup_converges_after_a_crash_at_any_step()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
            f.WithFile("plugin.dll", "plugin");            // 不在本次事务里的当前文件
            f.WithFile("plugin.dll.suold", "plugin-v0");   // 上一次更新遗留的陈旧备份
            File.WriteAllText(Path.Combine(f.Layout.DownloadCacheDirectory, "1.1.0.zip"), "zip");
            var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(n) };

            try
            {
                Recovery(f, fs).Run();
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            Recovery(f).Run();

            f.AssertStandardV110Files();
            Assert.Equal("plugin", f.Read("plugin.dll"));   // 当前文件不被陈旧备份覆盖、也不被删除
            Assert.Equal(new Version(1, 1, 0), f.ReadState().CurrentVersion);
            Assert.Empty(f.Leftovers());                    // 陈旧的 plugin.dll.suold 被 done 分支一并清掉
            Assert.Empty(f.CacheFiles());
            Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        }

        Assert.True(totalSteps >= 8, $"只扫描到 {totalSteps} 步");
    }

    // ---- Run() 永不抛出：任何 IO 层面的失败都进 Errors，不能让程序起不来 ----

    private static Func<FileOperation, Exception?> FailEnumerate(string pattern, Exception failure)
        => op => op.Kind == "EnumerateFiles" && string.Equals(op.SecondPath, pattern, StringComparison.Ordinal) ? failure : null;

    [Fact]
    public void Run_does_not_throw_when_the_journal_cannot_be_opened()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.FailAlways("OpenRead", "journal.json") };

        RecoveryResult result = Recovery(f, fs).Run();

        Assert.NotEmpty(result.Errors);
        Assert.Equal(JournalState.Committing, f.ReadJournal().Journal!.State);   // 读不出状态就不动磁盘，journal 原样保留
        Assert.Equal("exe-v2", f.Read("app.exe"));

        RecoveryResult retry = Recovery(f).Run();                                 // 故障消失后下次启动照常回滚
        Assert.Empty(retry.Errors);
        AssertCleanV1(f);
    }

    [Fact]
    public void Run_does_not_throw_when_enumerating_backups_fails_in_the_corrupt_branch()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        File.WriteAllText(f.Layout.JournalFile, "{ broken");
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FailEnumerate(SwapFileNames.OldSearchPattern, new UnauthorizedAccessException("模拟无权枚举")) };

        RecoveryResult result = Recovery(f, fs).Run();

        Assert.Contains(result.Errors, e => e.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
        Assert.Equal(JournalStatus.Corrupt, f.ReadJournal().Status);   // 枚举失败也是恢复失败：journal 保留
    }

    [Fact]
    public void Run_does_not_throw_when_enumerating_new_files_fails_in_the_preparing_branch()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        File.WriteAllText(f.Resolve("app.exe.sunew"), "half");
        SeedJournal(f, JournalState.Preparing);
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FailEnumerate(SwapFileNames.NewSearchPattern, new IOException("模拟枚举失败")) };

        RecoveryResult result = Recovery(f, fs).Run();

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Run_does_not_throw_when_clearing_the_download_cache_fails_in_the_done_branch()
    {
        using InstallationFixture f = FullyMovedButNotDone(JournalState.Done);
        File.WriteAllText(Path.Combine(f.Layout.DownloadCacheDirectory, "1.1.0.zip"), "zip");
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FailEnumerate("*", new IOException("模拟枚举失败")) };

        RecoveryResult result = Recovery(f, fs).Run();

        Assert.Equal(RecoveryAction.CompletedUpdate, result.Action);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);   // done 分支无论如何都删 journal
    }

    [Fact]
    public void Run_does_not_throw_on_a_journal_entry_whose_path_is_too_long()
    {
        using InstallationFixture f = InstallationFixture.StandardV1();
        UpdateJournal journal = StandardJournal(JournalState.Preparing);
        journal.Writes = [.. journal.Writes, new JournalEntry { Path = new string('a', 40_000), HadTarget = false }];
        var store = new JournalStore(f.Layout.JournalFile, Physical, NullUpdateLog.Instance);
        store.Write(journal);
        journal.State = JournalState.Committing;
        store.Write(journal);

        RecoveryResult result = Recovery(f).Run();   // PathTooLongException 是 IOException 不是 ArgumentException

        Assert.Equal(RecoveryAction.RolledBack, result.Action);
        Assert.NotEmpty(result.Errors);
    }

    // ---- 损坏 / 未识别的 journal：回滚有失败时保留 journal ----
    // journal 损坏时 *.suold 往往是唯一一份旧版本，删掉 journal 会让下次启动落入 Missing 分支不再回滚，
    // 旧文件永久遗失；保留只会让每次启动重试一次（幂等、无害），与 committing 分支的处置一致。

    [Fact]
    public void Corrupt_journal_with_a_locked_backup_keeps_the_journal_and_succeeds_on_retry()
    {
        using InstallationFixture f = FullyMovedButNotDone();
        File.WriteAllText(f.Layout.JournalFile, "{ broken");
        RecoveryResult first;
        using (new FileStream(f.Resolve("app.exe.suold"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            first = Recovery(f).Run();
        }

        Assert.Equal(RecoveryAction.RolledBack, first.Action);
        Assert.NotEmpty(first.Errors);
        Assert.Equal(JournalStatus.Corrupt, f.ReadJournal().Status);   // 保留

        RecoveryResult second = Recovery(f).Run();

        Assert.Equal(RecoveryAction.RolledBack, second.Action);
        Assert.Equal("exe-v1", f.Read("app.exe"));
        Assert.Equal(JournalStatus.Missing, f.ReadJournal().Status);
        Assert.Empty(f.Leftovers());
    }
}
