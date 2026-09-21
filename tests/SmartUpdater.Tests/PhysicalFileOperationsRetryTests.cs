using System.Diagnostics;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>
/// Move / Delete / OpenWrite 对"瞬时失败"的重试。背景：Defender 等实时扫描会在文件刚写完、刚改名后短暂持有它，
/// 这时改名报 ERROR_ACCESS_DENIED（UnauthorizedAccessException），删除和重新打开报 ERROR_SHARING_VIOLATION（IOException 0x80070020）。
/// 所有测试都注入睡眠函数，绝不真的等。
/// </summary>
public class PhysicalFileOperationsRetryTests
{
    private const int MaxAttempts = PhysicalFileOperations.MaxAttempts;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static (PhysicalFileOperations Fs, List<TimeSpan> Sleeps) Create()
    {
        var sleeps = new List<TimeSpan>();
        return (new PhysicalFileOperations(sleeps.Add), sleeps);
    }

    private static Exception Transient(string kind, string message) => kind switch
    {
        "denied" => new UnauthorizedAccessException(message),
        "sharing" => new IOException(message, unchecked((int)0x80070020)),
        "lock" => new IOException(message, unchecked((int)0x80070021)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static Exception NonTransient(string kind, string message) => kind switch
    {
        "file-not-found" => new FileNotFoundException(message),
        "dir-not-found" => new DirectoryNotFoundException(message),
        "disk-full" => new IOException(message, unchecked((int)0x80070070)),
        "already-exists" => new IOException(message, unchecked((int)0x800700B7)),
        "plain-io" => new IOException(message),
        "argument" => new ArgumentException(message),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // ---- 重试规则本身（不碰磁盘）----

    [Theory]
    [InlineData("denied", 1)]
    [InlineData("denied", 3)]
    [InlineData("denied", MaxAttempts - 1)]
    [InlineData("sharing", 1)]
    [InlineData("sharing", 5)]
    [InlineData("sharing", MaxAttempts - 1)]
    [InlineData("lock", 2)]
    [InlineData("lock", MaxAttempts - 1)]
    public void Transient_failure_is_retried_exactly_N_times_and_then_the_result_is_returned(string kind, int failures)
    {
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();
        int calls = 0;

        int result = fs.Retry(() =>
        {
            calls++;
            if (calls <= failures)
            {
                throw Transient(kind, $"attempt {calls}");
            }

            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(failures + 1, calls);
        Assert.Equal(failures, sleeps.Count);
    }

    [Fact]
    public void Retry_without_a_result_behaves_the_same()
    {
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();
        int calls = 0;

        fs.Retry(() =>
        {
            calls++;
            if (calls <= 2)
            {
                throw new UnauthorizedAccessException();
            }
        });

        Assert.Equal(3, calls);
        Assert.Equal(2, sleeps.Count);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("sharing")]
    [InlineData("lock")]
    public void Persistent_transient_failure_rethrows_the_last_exception_unchanged_within_the_budget(string kind)
    {
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();
        var thrown = new List<Exception>();

        Exception caught = Assert.ThrowsAny<Exception>(() => fs.Retry<int>(() =>
        {
            Exception next = Transient(kind, $"attempt {thrown.Count + 1}");
            thrown.Add(next);
            throw next;
        }));

        Assert.Equal(MaxAttempts, thrown.Count);
        Assert.Same(thrown[^1], caught);                       // 最后一次的那个异常对象，没有被包装、没有换成第一次的
        Assert.Equal(thrown[^1].GetType(), caught.GetType());
        Assert.Equal($"attempt {MaxAttempts}", caught.Message);
        Assert.Equal(MaxAttempts - 1, sleeps.Count);           // 最后一次失败后不再睡
        Assert.All(sleeps, d => Assert.True(d > TimeSpan.Zero));
        Assert.True(sleeps.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) <= Budget, "睡眠总和必须落在 2 秒预算内");
    }

    [Theory]
    [InlineData("file-not-found")]
    [InlineData("dir-not-found")]
    [InlineData("disk-full")]
    [InlineData("already-exists")]
    [InlineData("plain-io")]
    [InlineData("argument")]
    public void Non_transient_failures_are_thrown_at_once_without_any_sleep(string kind)
    {
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();
        int calls = 0;
        Exception original = NonTransient(kind, "boom");

        Exception caught = Assert.ThrowsAny<Exception>(() => fs.Retry<int>(() =>
        {
            calls++;
            throw original;
        }));

        Assert.Same(original, caught);
        Assert.Equal(1, calls);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void A_non_transient_failure_after_transient_ones_stops_the_retrying()
    {
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();
        int calls = 0;

        Assert.Throws<FileNotFoundException>(() => fs.Retry<int>(() =>
        {
            calls++;
            throw calls <= 2 ? new UnauthorizedAccessException() : new FileNotFoundException();
        }));

        Assert.Equal(3, calls);
        Assert.Equal(2, sleeps.Count);
    }

    [Fact]
    public void Production_instance_really_sleeps_between_attempts()
    {
        int calls = 0;
        var stopwatch = Stopwatch.StartNew();

        PhysicalFileOperations.Instance.Retry(() =>
        {
            if (++calls == 1)
            {
                throw new UnauthorizedAccessException();
            }
        });

        stopwatch.Stop();
        Assert.Equal(2, calls);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(5), $"生产实例应真的睡眠，实际只过了 {stopwatch.Elapsed}");
    }

    // ---- 接到真实文件系统上：Move / Delete / OpenWrite ----

    // 用 FileShare.None 持有目标模拟"杀软正扫着它"；第 3 次睡眠时放手，之后的重试才能成功，整个过程确定、不靠时序。
    private static (PhysicalFileOperations Fs, List<TimeSpan> Sleeps, Action<Stream> Hold) CreateWithReleaseOnThirdSleep()
    {
        var sleeps = new List<TimeSpan>();
        Stream? held = null;
        var fs = new PhysicalFileOperations(delay =>
        {
            sleeps.Add(delay);
            if (sleeps.Count == 3)
            {
                held?.Dispose();
            }
        });
        return (fs, sleeps, stream => held = stream);
    }

    [Fact]
    public void Move_over_a_temporarily_held_target_is_retried_until_it_is_released()
    {
        using var dir = new TempDirectory();
        string source = dir.WriteFile("s.tmp", "NEW");
        string target = dir.WriteFile("t.json", "OLD");
        (PhysicalFileOperations fs, List<TimeSpan> sleeps, Action<Stream> hold) = CreateWithReleaseOnThirdSleep();
        hold(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None));

        fs.Move(source, target, overwrite: true);

        Assert.Equal(3, sleeps.Count);
        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Move_without_overwrite_of_a_temporarily_held_source_is_retried_until_it_is_released()
    {
        using var dir = new TempDirectory();
        string source = dir.WriteFile("a.sunew", "NEW");
        string target = dir.Resolve("a.exe");
        (PhysicalFileOperations fs, List<TimeSpan> sleeps, Action<Stream> hold) = CreateWithReleaseOnThirdSleep();
        hold(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None));

        fs.Move(source, target, overwrite: false);

        Assert.Equal(3, sleeps.Count);
        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void Delete_of_a_temporarily_held_file_is_retried_until_it_is_released()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("x.suold", "X");
        (PhysicalFileOperations fs, List<TimeSpan> sleeps, Action<Stream> hold) = CreateWithReleaseOnThirdSleep();
        hold(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None));

        fs.Delete(file);

        Assert.Equal(3, sleeps.Count);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void OpenWrite_of_a_temporarily_held_file_is_retried_until_it_is_released()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("state.json.tmp", "STALE");
        (PhysicalFileOperations fs, List<TimeSpan> sleeps, Action<Stream> hold) = CreateWithReleaseOnThirdSleep();
        hold(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None));

        using (Stream stream = fs.OpenWrite(file))
        {
            stream.Write("FRESH"u8);
        }

        Assert.Equal(3, sleeps.Count);
        Assert.Equal("FRESH", File.ReadAllText(file));
    }

    [Fact]
    public void Move_onto_a_genuinely_read_only_target_still_fails_with_UnauthorizedAccessException()
    {
        using var dir = new TempDirectory();
        string source = dir.WriteFile("s.tmp", "NEW");
        string target = dir.WriteFile("t.json", "OLD");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => fs.Move(source, target, overwrite: true));

            Assert.Equal(MaxAttempts - 1, sleeps.Count);        // 把预算用完才放弃（慢，但类型不变）
            Assert.Equal("OLD", File.ReadAllText(target));
            Assert.Equal("NEW", File.ReadAllText(source));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Missing_paths_fail_at_once_with_their_original_exception_and_never_sleep()
    {
        using var dir = new TempDirectory();
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();

        Assert.Throws<FileNotFoundException>(() => fs.Move(dir.Resolve("nope.tmp"), dir.Resolve("t.json"), overwrite: true));
        string existing = dir.WriteFile("exists.tmp", "X");
        Assert.Throws<DirectoryNotFoundException>(() => fs.Move(existing, dir.Resolve("no-such-dir/t.json"), overwrite: true));
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenWrite(dir.Resolve("no-such-dir/f.tmp")));
        fs.Delete(dir.Resolve("nope.txt"));
        fs.Delete(dir.Resolve("no-such-dir/nope.txt"));

        Assert.Empty(sleeps);
    }

    [Fact]
    public void Move_to_an_existing_destination_without_overwrite_fails_at_once_and_never_sleeps()
    {
        using var dir = new TempDirectory();
        string a = dir.WriteFile("a.txt", "A");
        string b = dir.WriteFile("b.txt", "B");
        (PhysicalFileOperations fs, List<TimeSpan> sleeps) = Create();

        Assert.Throws<IOException>(() => fs.Move(a, b, overwrite: false));

        Assert.Empty(sleeps);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }
}
