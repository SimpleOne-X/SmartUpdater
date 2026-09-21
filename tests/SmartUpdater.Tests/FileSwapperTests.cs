using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class FileSwapperTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;

    // 标准场景：a.exe 被替换、b.dll 被删除、c.dll 新增。
    private static IReadOnlyList<SwapOperation> StandardOperations(TempDirectory dir) =>
    [
        new(SwapKind.Delete, dir.Resolve("b.dll"), HadTarget: true),
        new(SwapKind.Write, dir.Resolve("a.exe"), HadTarget: true),
        new(SwapKind.Write, dir.Resolve("c.dll"), HadTarget: false),
    ];

    private static void SeedStandard(TempDirectory dir)
    {
        dir.WriteFile("a.exe", "a-old");
        dir.WriteFile("a.exe.sunew", "a-new");
        dir.WriteFile("b.dll", "b-old");
        dir.WriteFile("c.dll.sunew", "c-new");
    }

    // 上一轮更新遗留的备份（.suold 保留到新版本成功启动后才删，遗留是常态）。
    private static void SeedStaleBackups(TempDirectory dir)
    {
        dir.WriteFile("a.exe.suold", "stale");
        dir.WriteFile("b.dll.suold", "stale");
    }

    // allowStaleBackups：种过陈旧备份时，回滚后 .suold 要么已被提交步骤清掉（不存在），要么原样留着（内容仍是 "stale"），
    // 但绝不能被当成"旧版本"盖回目标文件——目标文件的内容由上面的断言把关。
    private static void AssertOldState(TempDirectory dir, bool allowStaleBackups = false)
    {
        Assert.Equal("a-old", dir.ReadFile("a.exe"));
        Assert.Equal("b-old", dir.ReadFile("b.dll"));
        Assert.False(dir.Exists("c.dll"));
        Assert.False(dir.Exists("c.dll.suold"));

        if (allowStaleBackups)
        {
            foreach (string backup in new[] { "a.exe.suold", "b.dll.suold" })
            {
                if (dir.Exists(backup))
                {
                    Assert.Equal("stale", dir.ReadFile(backup));
                }
            }
        }
        else
        {
            Assert.False(dir.Exists("a.exe.suold"));
            Assert.False(dir.Exists("b.dll.suold"));
        }
    }

    private static void AssertNewState(TempDirectory dir)
    {
        Assert.Equal("a-new", dir.ReadFile("a.exe"));
        Assert.Equal("a-old", dir.ReadFile("a.exe.suold"));
        Assert.False(dir.Exists("b.dll"));
        Assert.Equal("b-old", dir.ReadFile("b.dll.suold"));
        Assert.Equal("c-new", dir.ReadFile("c.dll"));
        Assert.False(dir.Exists("c.dll.suold"));
        Assert.False(dir.Exists("a.exe.sunew"));
        Assert.False(dir.Exists("c.dll.sunew"));
    }

    [Fact]
    public void Commit_replaces_deletes_and_adds_using_renames_only()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var fs = new FaultInjectingFileOperations(Physical);
        var log = new RecordingLog();

        new FileSwapper(fs, log).Commit(StandardOperations(dir));

        AssertNewState(dir);
        string[] moves = [.. fs.MutatingOperations.Select(o => $"{o.Kind} {Path.GetFileName(o.Path)} -> {Path.GetFileName(o.SecondPath)}")];
        Assert.Equal(
            new[]
            {
                "Move b.dll -> b.dll.suold",
                "Move a.exe -> a.exe.suold",
                "Move a.exe.sunew -> a.exe",
                "Move c.dll.sunew -> c.dll",
            },
            moves);
        Assert.True(log.Contains("a.exe"));
        Assert.True(log.Contains("b.dll"));
        Assert.True(log.Contains("c.dll"));
    }

    [Fact]
    public void Delete_of_missing_target_is_a_no_op_and_is_logged_as_skipped()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();

        new FileSwapper(Physical, log).Commit([new SwapOperation(SwapKind.Delete, dir.Resolve("gone.dll"), HadTarget: true)]);

        Assert.False(dir.Exists("gone.dll.suold"));
        Assert.Contains(log.Entries, e => e.Message.Contains("gone.dll", StringComparison.Ordinal) && e.Message.Contains("跳过", StringComparison.Ordinal));
    }

    [Fact]
    public void Commit_refuses_to_start_when_any_pending_file_is_missing()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        File.Delete(dir.Resolve("c.dll.sunew"));

        Assert.Throws<InvalidOperationException>(() => new FileSwapper(Physical, new RecordingLog()).Commit(StandardOperations(dir)));

        Assert.Equal("a-old", dir.ReadFile("a.exe"));
        Assert.Equal("b-old", dir.ReadFile("b.dll"));
        Assert.False(dir.Exists("b.dll.suold"));
        Assert.False(dir.Exists("a.exe.suold"));
    }

    [Fact]
    public void Stale_suold_is_removed_before_the_rename()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        dir.WriteFile("a.exe.suold", "stale");
        dir.WriteFile("b.dll.suold", "stale");

        new FileSwapper(Physical, new RecordingLog()).Commit(StandardOperations(dir));

        Assert.Equal("a-old", dir.ReadFile("a.exe.suold"));
        Assert.Equal("b-old", dir.ReadFile("b.dll.suold"));
    }

    [Fact]
    public void Read_only_target_is_swapped_and_keeps_its_attribute_on_the_backup()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        File.SetAttributes(dir.Resolve("a.exe"), FileAttributes.ReadOnly);

        new FileSwapper(Physical, new RecordingLog()).Commit(StandardOperations(dir));

        Assert.Equal("a-new", dir.ReadFile("a.exe"));
        Assert.True(File.GetAttributes(dir.Resolve("a.exe.suold")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Failure_midway_rolls_back_everything_already_started()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailOnce("Move", "c.dll.sunew"),
        };
        var log = new RecordingLog();

        var ex = Assert.Throws<SwapFailedException>(() => new FileSwapper(fs, log).Commit(StandardOperations(dir)));

        Assert.True(ex.IsRolledBack);
        Assert.Equal(3, ex.FailedOperationIndex);
        Assert.IsType<IOException>(ex.CommitFailure);
        Assert.Same(ex.CommitFailure, ex.InnerException);
        AssertOldState(dir);
        Assert.Equal("a-new", dir.ReadFile("a.exe.sunew"));
        Assert.Equal("c-new", dir.ReadFile("c.dll.sunew"));
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Error));
    }

    [Fact]
    public void Failure_between_the_two_moves_of_a_write_restores_that_file()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailOnce("Move", "a.exe.sunew"),
        };

        var ex = Assert.Throws<SwapFailedException>(() => new FileSwapper(fs, new RecordingLog()).Commit(StandardOperations(dir)));

        Assert.True(ex.IsRolledBack);
        Assert.Equal(2, ex.FailedOperationIndex);
        AssertOldState(dir);
    }

    [Fact]
    public void Rollback_failures_are_collected_and_the_remaining_operations_are_still_rolled_back()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.Any(
                FaultInjectingFileOperations.FailOnce("Move", "c.dll.sunew"),
                FaultInjectingFileOperations.FailAlways("Move", "a.exe.suold")),
        };
        var log = new RecordingLog();

        var ex = Assert.Throws<SwapFailedException>(() => new FileSwapper(fs, log).Commit(StandardOperations(dir)));

        Assert.False(ex.IsRolledBack);
        IOException failure = Assert.IsType<IOException>(Assert.Single(ex.RollbackFailures));
        Assert.Contains("a.exe.suold", failure.Message, StringComparison.Ordinal);
        Assert.Equal("b-old", dir.ReadFile("b.dll"));          // 逆序里排在 a.exe 之后的 b.dll 仍被恢复
        Assert.False(dir.Exists("b.dll.suold"));
        Assert.False(dir.Exists("a.exe"));                     // a.exe 停在 .suold，等启动恢复重试
        Assert.Equal("a-old", dir.ReadFile("a.exe.suold"));
        Assert.True(log.AtLevel(UpdateLogLevel.Error).Count() >= 2);
    }

    [Fact]
    public void Rollback_after_a_complete_commit_restores_the_previous_state()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var swapper = new FileSwapper(Physical, new RecordingLog());
        IReadOnlyList<SwapOperation> ops = StandardOperations(dir);
        swapper.Commit(ops);

        IReadOnlyList<Exception> failures = swapper.Rollback(ops);

        Assert.Empty(failures);
        AssertOldState(dir);
        Assert.Equal("a-new", dir.ReadFile("a.exe.sunew"));   // 新文件改名回 .sunew，等 DeletePendingFiles
        Assert.Equal("c-new", dir.ReadFile("c.dll.sunew"));

        Assert.Empty(swapper.DeletePendingFiles(ops));

        Assert.False(dir.Exists("a.exe.sunew"));
        Assert.False(dir.Exists("c.dll.sunew"));
    }

    [Fact]
    public void Rollback_is_idempotent()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var swapper = new FileSwapper(Physical, new RecordingLog());
        IReadOnlyList<SwapOperation> ops = StandardOperations(dir);
        swapper.Commit(ops);

        Assert.Empty(swapper.Rollback(ops));
        Assert.Empty(swapper.Rollback(ops));
        Assert.Empty(swapper.DeletePendingFiles(ops));
        Assert.Empty(swapper.DeletePendingFiles(ops));

        AssertOldState(dir);
    }

    // 恢复流程是 Rollback -> DeletePendingFiles -> 删 journal。崩在后两步之间，下次启动会对"已回滚、.sunew 已删"的磁盘
    // 再回滚一次：此时 target 在、.sunew 不在、.suold 不在，把它当成"已完成的写入"就会把当前文件移走。必须一个文件操作都不做。
    [Fact]
    public void Rollback_after_the_pending_files_were_deleted_changes_nothing()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var swapper = new FileSwapper(Physical, new RecordingLog());
        IReadOnlyList<SwapOperation> ops = StandardOperations(dir);
        swapper.Commit(ops);
        Assert.Empty(swapper.Rollback(ops));
        Assert.Empty(swapper.DeletePendingFiles(ops));
        var fs = new FaultInjectingFileOperations(Physical);

        Assert.Empty(new FileSwapper(fs, new RecordingLog()).Rollback(ops));

        Assert.Empty(fs.MutatingOperations);
        AssertOldState(dir);
        Assert.False(dir.Exists("a.exe.sunew"));
        Assert.False(dir.Exists("c.dll.sunew"));
    }

    [Fact]
    public void Rollback_on_untouched_operations_changes_nothing()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        var swapper = new FileSwapper(Physical, new RecordingLog());

        Assert.Empty(swapper.Rollback(StandardOperations(dir)));

        Assert.Equal("a-old", dir.ReadFile("a.exe"));
        Assert.Equal("a-new", dir.ReadFile("a.exe.sunew"));
        Assert.Equal("b-old", dir.ReadFile("b.dll"));
        Assert.Equal("c-new", dir.ReadFile("c.dll.sunew"));
    }

    // 只回滚、不提交：全部操作都"未开始"，而 a.exe / b.dll 各带着上一轮遗留的陈旧 .suold。
    // 不能把 a.exe.suold 当成"被本次提交备份的旧版本"盖回 a.exe，否则随后 DeletePendingFiles 会删掉停在 .sunew 里的当前版本。
    [Fact]
    public void Rollback_on_untouched_operations_with_a_stale_suold_changes_nothing()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        SeedStaleBackups(dir);
        var swapper = new FileSwapper(Physical, new RecordingLog());
        IReadOnlyList<SwapOperation> ops = StandardOperations(dir);

        Assert.Empty(swapper.Rollback(ops));

        Assert.Equal("a-old", dir.ReadFile("a.exe"));
        Assert.Equal("a-new", dir.ReadFile("a.exe.sunew"));
        Assert.Equal("stale", dir.ReadFile("a.exe.suold"));
        Assert.Equal("b-old", dir.ReadFile("b.dll"));
        Assert.Equal("stale", dir.ReadFile("b.dll.suold"));
        Assert.False(dir.Exists("c.dll"));
        Assert.Equal("c-new", dir.ReadFile("c.dll.sunew"));

        // 调用方在回滚之后就是这样收尾的：数据丢失发生在这一步
        Assert.Empty(swapper.DeletePendingFiles(ops));

        Assert.Equal("a-old", dir.ReadFile("a.exe"));
        Assert.Equal("stale", dir.ReadFile("a.exe.suold"));
        Assert.Equal("b-old", dir.ReadFile("b.dll"));
        Assert.False(dir.Exists("a.exe.sunew"));
        Assert.False(dir.Exists("c.dll.sunew"));
    }

    // 回滚必须逆序：提交顺序是刻意的"deletes → writes → manifest → 主 exe"，逆序让主 exe 最先被还原，
    // 回滚死在半路时留下的是"主 exe 已回到旧版、可启动"，而不是半换状态。
    [Fact]
    public void Rollback_undoes_operations_in_reverse_order()
    {
        using var dir = new TempDirectory();
        SeedStandard(dir);
        IReadOnlyList<SwapOperation> ops = StandardOperations(dir);
        new FileSwapper(Physical, new RecordingLog()).Commit(ops);
        var fs = new FaultInjectingFileOperations(Physical);

        Assert.Empty(new FileSwapper(fs, new RecordingLog()).Rollback(ops));

        string[] moves = [.. fs.MutatingOperations.Select(o => $"{o.Kind} {Path.GetFileName(o.Path)} -> {Path.GetFileName(o.SecondPath)}")];
        Assert.Equal(
            new[]
            {
                "Move c.dll -> c.dll.sunew",
                "Move a.exe -> a.exe.sunew",
                "Move a.exe.suold -> a.exe",
                "Move b.dll.suold -> b.dll",
            },
            moves);
    }

    // 崩溃扫描：提交的任一操作之后断电，只凭磁盘 + journal 里的 HadTarget 做状态化回滚，必须回到完整的旧版本。
    // withStaleBackups = true：同样的扫描，但 a.exe / b.dll 起初就带着上一轮遗留的陈旧 .suold。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crash_at_any_commit_step_is_fully_rolled_back_by_a_fresh_swapper(bool withStaleBackups)
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using var dir = new TempDirectory();
            SeedStandard(dir);
            if (withStaleBackups)
            {
                SeedStaleBackups(dir);
            }

            IReadOnlyList<SwapOperation> ops = StandardOperations(dir);
            var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(n) };

            try
            {
                new FileSwapper(fs, new RecordingLog()).Commit(ops);
            }
            catch (SwapFailedException)
            {
                // 崩溃后的回滚尝试全部失败是预期的：磁盘"不再响应"
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            var fresh = new FileSwapper(Physical, new RecordingLog());
            Assert.Empty(fresh.Rollback(ops));
            Assert.Empty(fresh.DeletePendingFiles(ops));
            AssertOldState(dir, allowStaleBackups: withStaleBackups);
            Assert.False(dir.Exists("a.exe.sunew"), $"崩溃于第 {n} 步（{fs.Operations[^1]}）后 .sunew 残留");
            Assert.False(dir.Exists("c.dll.sunew"));
        }

        Assert.True(totalSteps >= 8, $"只扫描到 {totalSteps} 步");
    }
}
