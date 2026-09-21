using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

// 装饰器本身的语义。所有崩溃测试都建在它上面：它若悄悄失效（比如崩溃后不再全部抛出），
// 依赖它的测试会空转通过，所以关键语义在这里单独钉死。
public class FaultInjectingFileOperationsTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;

    [Fact]
    public void A_simulated_crash_is_sticky_and_the_disk_stays_as_it_was_at_that_moment()
    {
        using var dir = new TempDirectory();
        string a = dir.WriteFile("a.txt", "A");
        string b = dir.WriteFile("b.txt", "B");
        string c = dir.WriteFile("c.txt", "C");
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(2) };

        fs.Delete(a);
        Assert.Throws<SimulatedCrashException>(() => fs.Delete(b));
        Assert.Throws<SimulatedCrashException>(() => fs.Delete(c));
        Assert.Throws<SimulatedCrashException>(() => fs.FileExists(c));

        Assert.True(fs.Crashed);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(c));

        // 每一次尝试（崩溃当次与崩溃之后的）在抛出前都已记录，序号从 1 递增。
        string[] expected = [$"1: Delete {a}", $"2: Delete {b}", $"3: Delete {c}", $"4: FileExists {c}"];
        string[] actual = [.. fs.Operations.Select(o => o.ToString())];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_crash_inside_a_write_still_releases_the_file_handle()
    {
        using var dir = new TempDirectory();
        string file = dir.Resolve("f.bin");
        var fs = new FaultInjectingFileOperations(Physical) { Policy = FaultInjectingFileOperations.CrashAt(2) };

        Assert.Throws<SimulatedCrashException>(() =>
        {
            using Stream stream = fs.OpenWrite(file);
            stream.Write("abc"u8);
            fs.FlushToDisk(stream);
        });

        // OpenWrite 是独占打开：句柄若泄漏，这里会因共享冲突抛 IOException。
        File.Delete(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void OnOperation_runs_after_the_policy_lets_an_operation_through_and_before_it_executes()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("f.txt", "x");
        var seen = new List<string>();
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailAlways("Delete", "g.txt"),
            OnOperation = op =>
            {
                seen.Add(op.Kind);
                if (op.Kind == "FileExists")
                {
                    File.Delete(file);
                }
            },
        };

        // 观察点先把文件删掉，随后真正执行的 FileExists 看到的就是被篡改后的磁盘。
        Assert.False(fs.FileExists(file));

        // 被策略拒绝的操作不会走到观察点。
        Assert.Throws<IOException>(() => fs.Delete(dir.Resolve("g.txt")));

        Assert.Equal(["FileExists"], seen);
    }

    [Fact]
    public void FailAlways_matches_kind_and_path_suffix_ignoring_case_and_is_not_a_crash()
    {
        using var dir = new TempDirectory();
        string bad = dir.WriteFile("x.sunew", "1");
        string fine = dir.WriteFile("y.txt", "2");
        var fs = new FaultInjectingFileOperations(Physical)
        {
            Policy = FaultInjectingFileOperations.FailAlways("Delete", ".SUNEW"),
        };

        Assert.Throws<IOException>(() => fs.Delete(bad));
        Assert.Throws<IOException>(() => fs.Delete(bad));
        fs.Delete(fine);
        Assert.True(fs.FileExists(bad));

        Assert.True(File.Exists(bad));
        Assert.False(File.Exists(fine));
        Assert.False(fs.Crashed);
    }

    [Fact]
    public void Any_reports_the_first_failing_policy_and_passes_when_none_fail()
    {
        Func<FileOperation, Exception?> policy = FaultInjectingFileOperations.Any(
            FaultInjectingFileOperations.FailOnce("Delete", "a.txt"),
            FaultInjectingFileOperations.FailAlways("Move", "b.txt"));

        Assert.IsType<IOException>(policy(new FileOperation(1, "Delete", @"C:\x\a.txt")));
        Assert.Null(policy(new FileOperation(2, "Delete", @"C:\x\a.txt")));
        Assert.IsType<IOException>(policy(new FileOperation(3, "Move", @"C:\x\b.txt", @"C:\x\c.txt")));
        Assert.IsType<IOException>(policy(new FileOperation(4, "Move", @"C:\x\b.txt", @"C:\x\d.txt")));
        Assert.Null(policy(new FileOperation(5, "OpenRead", @"C:\x\b.txt")));
    }

    [Fact]
    public void MutatingOperations_keeps_only_operations_that_change_the_disk_and_Move_records_the_source_as_Path()
    {
        using var dir = new TempDirectory();
        string sub = dir.Resolve("sub");
        string file = Path.Combine(sub, "f.txt");
        string moved = Path.Combine(sub, "g.txt");
        var fs = new FaultInjectingFileOperations(Physical);

        fs.CreateDirectory(sub);
        using (Stream stream = fs.OpenWrite(file))
        {
            stream.Write("x"u8);
            fs.FlushToDisk(stream);
        }

        _ = fs.FileExists(file);
        _ = fs.DirectoryExists(sub);
        _ = fs.GetFileLength(file);
        FileAttributes attributes = fs.GetAttributes(file);
        fs.SetAttributes(file, attributes);
        _ = fs.EnumerateFiles(sub, "*", recursive: false);
        using (fs.OpenRead(file))
        {
        }

        fs.Move(file, moved, overwrite: false);
        fs.Delete(moved);

        string[] expectedKinds = ["CreateDirectory", "OpenWrite", "FlushToDisk", "SetAttributes", "Move", "Delete"];
        string[] mutatingKinds = [.. fs.MutatingOperations.Select(o => o.Kind)];
        Assert.Equal(expectedKinds, mutatingKinds);

        int[] indexes = [.. fs.Operations.Select(o => o.Index)];
        int[] oneBased = [.. Enumerable.Range(1, fs.Operations.Count)];
        Assert.Equal(oneBased, indexes);

        // FailOnce / FailAlways 按 op.Path 匹配：Move 的 Path 是源，目标在 SecondPath（崩溃测试都依赖这一约定）。
        FileOperation move = Assert.Single(fs.Operations, o => o.Kind == "Move");
        Assert.Equal(file, move.Path);
        Assert.Equal(moved, move.SecondPath);

        // FlushToDisk 记录的是所属文件的路径，不是流。
        Assert.Equal(file, Assert.Single(fs.Operations, o => o.Kind == "FlushToDisk").Path);
    }
}
