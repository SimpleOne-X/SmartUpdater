using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class FileLoggerTests
{
    // 2026-09-18 10:00:00Z = 2026-09-18 18:00:00 (UTC+8)
    private static readonly DateTimeOffset Noon = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Clock() => new(Noon);

    // 日志器还开着文件（写句柄，FileShare.ReadWrite）。File.ReadAllLines 以 FileShare.Read 打开，
    // 与仍存活的写句柄冲突（IOException），所以这里显式允许读写共享；行的切分与 ReadAllLines 相同（StreamReader.ReadLine）。
    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    [Fact]
    public void Writes_one_line_per_entry_in_the_documented_format()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        logger.Write(UpdateLogLevel.Information, UpdateStage.Commit, "write app.exe");

        Assert.Equal(dir.Resolve("logs"), logger.ActiveDirectory);
        Assert.Equal(dir.Resolve("logs/updater-20260918.log"), logger.CurrentFilePath);
        Assert.Equal(
            new[] { "2026-09-18T10:00:00.000Z | 2026-09-18 18:00:00.000 +08:00 | INFO | Commit | write app.exe" },
            Lines(logger.CurrentFilePath!));
    }

    [Theory]
    [InlineData(UpdateLogLevel.Debug, null, "DEBUG | -")]
    [InlineData(UpdateLogLevel.Information, UpdateStage.Check, "INFO | Check")]
    [InlineData(UpdateLogLevel.Warning, UpdateStage.DiskCheck, "WARN | DiskCheck")]
    [InlineData(UpdateLogLevel.Error, UpdateStage.Rollback, "ERROR | Rollback")]
    public void FormatLine_uses_fixed_level_names_and_dash_for_no_stage(UpdateLogLevel level, UpdateStage? stage, string expectedMiddle)
    {
        string line = FileLogger.FormatLine(Noon, FakeTimeProvider.EastEight, level, stage, "msg");

        Assert.Equal($"2026-09-18T10:00:00.000Z | 2026-09-18 18:00:00.000 +08:00 | {expectedMiddle} | msg", line);
    }

    [Fact]
    public void Line_breaks_inside_the_message_are_flattened()
    {
        string line = FileLogger.FormatLine(Noon, FakeTimeProvider.EastEight, UpdateLogLevel.Information, null, "a\r\nb\nc");

        Assert.EndsWith("| a b c", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Exception_is_appended_as_indented_continuation_lines()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        Exception captured;
        try
        {
            throw new InvalidOperationException("boom", new IOException("inner"));
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        logger.Write(UpdateLogLevel.Error, UpdateStage.Commit, "failed", captured);
        logger.Write(UpdateLogLevel.Information, null, "next entry");

        string[] lines = Lines(logger.CurrentFilePath!);
        Assert.StartsWith("2026-09-18T10:00:00.000Z", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("    System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("    ", StringComparison.Ordinal) && l.Contains("System.IO.IOException: inner", StringComparison.Ordinal));
        Assert.All(lines.Skip(1).SkipLast(1), l => Assert.StartsWith("    ", l, StringComparison.Ordinal));
        Assert.StartsWith("2026-09-18T10:00:00.000Z", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("| next entry", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Warning, Clock());

        logger.Write(UpdateLogLevel.Information, null, "quiet");
        logger.Write(UpdateLogLevel.Debug, null, "quieter");
        Assert.Equal(0, new FileInfo(logger.CurrentFilePath!).Length);

        logger.Write(UpdateLogLevel.Warning, null, "loud");
        Assert.Single(Lines(logger.CurrentFilePath!));
    }

    [Fact]
    public void Every_entry_is_visible_to_other_readers_immediately()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        logger.Write(UpdateLogLevel.Information, null, "flushed");

        using var reader = new FileStream(logger.CurrentFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader, Encoding.UTF8);
        Assert.EndsWith("| flushed", text.ReadToEnd().TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rolls_to_a_new_file_when_the_local_date_changes_even_if_utc_date_does_not()
    {
        using var dir = new TempDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 15, 59, 59, TimeSpan.Zero));   // 23:59:59 本地
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, clock);

        logger.Write(UpdateLogLevel.Information, null, "before midnight");
        clock.Advance(TimeSpan.FromSeconds(1));                                                          // 00:00:00 本地，UTC 仍是 9-18
        logger.Write(UpdateLogLevel.Information, null, "after midnight");

        Assert.Equal(dir.Resolve("logs/updater-20260919.log"), logger.CurrentFilePath);
        Assert.Single(Lines(dir.Resolve("logs/updater-20260918.log")));
        Assert.Single(Lines(dir.Resolve("logs/updater-20260919.log")));
        Assert.EndsWith("| after midnight", Lines(dir.Resolve("logs/updater-20260919.log"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Files_older_than_seven_days_are_deleted_on_startup_and_unknown_names_are_kept()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("logs/updater-20260910.log", "8 days old");
        dir.WriteFile("logs/updater-20260911.log", "7 days old");
        dir.WriteFile("logs/updater-20260912.log", "6 days old");
        dir.WriteFile("logs/updater-garbage.log", "unparsable");
        dir.WriteFile("logs/other.txt", "not ours");

        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        Assert.False(dir.Exists("logs/updater-20260910.log"));
        Assert.False(dir.Exists("logs/updater-20260911.log"));
        Assert.True(dir.Exists("logs/updater-20260912.log"));
        Assert.True(dir.Exists("logs/updater-garbage.log"));
        Assert.True(dir.Exists("logs/other.txt"));
    }

    [Fact]
    public void Stops_after_the_single_file_cap_with_one_sentinel_and_resumes_next_day()
    {
        using var dir = new TempDirectory();
        var clock = Clock();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, clock, maxFileBytes: 250);
        string thirty = new('x', 30);

        for (int i = 1; i <= 5; i++)
        {
            logger.Write(UpdateLogLevel.Information, null, $"{i:00} {thirty}");
        }

        string[] today = Lines(dir.Resolve("logs/updater-20260918.log"));
        Assert.Equal(4, today.Length);                                     // 3 条正常 + 1 条哨兵
        Assert.EndsWith("| 03 " + thirty, today[2], StringComparison.Ordinal);
        Assert.Contains("| WARN | - |", today[3], StringComparison.Ordinal);
        Assert.Contains("上限", today[3], StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromDays(1));
        logger.Write(UpdateLogLevel.Information, null, "new day");

        Assert.Single(Lines(dir.Resolve("logs/updater-20260919.log")));
    }

    [Fact]
    public void Falls_back_to_the_secondary_directory_when_the_primary_cannot_be_created()
    {
        using var dir = new TempDirectory();
        string primaryBlockedByFile = dir.WriteFile("app/.smartupdater/logs", "a file where the directory should be");

        using var logger = new FileLogger(primaryBlockedByFile, dir.Resolve("fallback/logs"), UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Information, null, "hello");

        Assert.Equal(dir.Resolve("fallback/logs"), logger.ActiveDirectory);
        Assert.Single(Lines(dir.Resolve("fallback/logs/updater-20260918.log")));
    }

    [Fact]
    public void Is_disabled_without_throwing_when_both_directories_are_unusable()
    {
        using var dir = new TempDirectory();
        string blockedPrimary = dir.WriteFile("a/logs", "file");
        string blockedFallback = dir.WriteFile("b/logs", "file");

        using var logger = new FileLogger(blockedPrimary, blockedFallback, UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Error, UpdateStage.Commit, "nowhere to go");

        Assert.Null(logger.ActiveDirectory);
        Assert.Null(logger.CurrentFilePath);
    }

    [Fact]
    public void Concurrent_writers_never_interleave_within_a_line()
    {
        using var dir = new TempDirectory();
        const int threads = 8;
        const int perThread = 50;
        using var barrier = new Barrier(threads);
        string path;

        using (var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock()))
        {
            path = logger.CurrentFilePath!;
            // 用专用线程而不是 Task.Run：线程池注入线程有延迟，8 个任务在 Barrier 上互等可能拖很久
            // 裸线程里没被接住的异常会杀掉整个测试宿主进程：收集起来，在测试线程上抛出，只让这一个测试失败
            var writerFailures = new ConcurrentBag<Exception>();
            Thread[] writers = [.. Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < perThread; i++)
                    {
                        logger.Write(UpdateLogLevel.Information, null, $"thread-{t} line {i}");
                    }
                }
                catch (Exception ex)
                {
                    writerFailures.Add(ex);
                }
            }))];
            foreach (Thread writer in writers)
            {
                writer.Start();
            }

            foreach (Thread writer in writers)
            {
                writer.Join();
            }

            if (!writerFailures.IsEmpty)
            {
                throw new AggregateException("写入线程抛出了异常", writerFailures);
            }
        }

        string[] lines = Lines(path);
        var pattern = new Regex(@"^2026-09-18T10:00:00\.000Z \| 2026-09-18 18:00:00\.000 \+08:00 \| INFO \| - \| thread-\d line \d+$");
        Assert.Equal(threads * perThread, lines.Length);
        Assert.All(lines, l => Assert.Matches(pattern, l));
    }

    [Fact]
    public void FileNameFor_uses_the_documented_pattern()
    {
        Assert.Equal("updater-20260918.log", FileLogger.FileNameFor(new DateOnly(2026, 9, 18)));
    }

    [Fact]
    public void File_name_and_line_format_do_not_depend_on_the_current_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");   // 佛历：2026 年会被格式化成 2569

            Assert.Equal("updater-20260918.log", FileLogger.FileNameFor(new DateOnly(2026, 9, 18)));
            Assert.Equal(
                "2026-09-18T10:00:00.000Z | 2026-09-18 18:00:00.000 +08:00 | INFO | - | msg",
                FileLogger.FormatLine(Noon, FakeTimeProvider.EastEight, UpdateLogLevel.Information, null, "msg"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Syntactically_invalid_directories_are_unusable_not_fatal()
    {
        using var dir = new TempDirectory();

        using var bothInvalid = new FileLogger("bad\0primary", "bad\0fallback", UpdateLogLevel.Debug, Clock());
        bothInvalid.Write(UpdateLogLevel.Information, null, "nowhere");
        Assert.Null(bothInvalid.ActiveDirectory);

        using var emptyPrimary = new FileLogger(string.Empty, dir.Resolve("fallback"), UpdateLogLevel.Debug, Clock());
        emptyPrimary.Write(UpdateLogLevel.Information, null, "hello");
        Assert.Equal(dir.Resolve("fallback"), emptyPrimary.ActiveDirectory);
        Assert.Single(Lines(dir.Resolve("fallback/updater-20260918.log")));
    }

    [Fact]
    public void An_expired_file_that_cannot_be_deleted_is_reported_as_a_warning_and_kept()
    {
        using var dir = new TempDirectory();
        string expired = dir.WriteFile("logs/updater-20260901.log", "old");
        using var holder = new FileStream(expired, FileMode.Open, FileAccess.Read, FileShare.None);

        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        Assert.True(dir.Exists("logs/updater-20260901.log"));
        string warning = Assert.Single(Lines(logger.CurrentFilePath!), l => l.Contains("| WARN | - |", StringComparison.Ordinal));
        Assert.Contains("updater-20260901.log", warning, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]   // FileStream.Lock 的强制字节范围锁只在 Windows 上会让别的句柄写失败
    public void A_failing_write_switches_to_the_fallback_directory_and_records_why()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("primary"), dir.Resolve("fallback"), UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Information, null, "first");

        // 另一个句柄给整个文件加字节范围锁：Windows 上日志器的下一次写入会以 IOException 失败
        using (var blocker = new FileStream(dir.Resolve("primary/updater-20260918.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            blocker.Lock(0, long.MaxValue / 2);
            logger.Write(UpdateLogLevel.Information, null, "second");
        }

        Assert.Equal(dir.Resolve("fallback"), logger.ActiveDirectory);
        Assert.Equal(dir.Resolve("fallback/updater-20260918.log"), logger.CurrentFilePath);
        string[] entries = [.. Lines(logger.CurrentFilePath!).Where(l => !l.StartsWith(' '))];
        Assert.Equal(2, entries.Length);
        Assert.Contains("| WARN | - |", entries[0], StringComparison.Ordinal);
        Assert.Contains("已切换到回退目录", entries[0], StringComparison.Ordinal);
        Assert.EndsWith("| second", entries[1], StringComparison.Ordinal);
        Assert.EndsWith("| first", Assert.Single(Lines(dir.Resolve("primary/updater-20260918.log"))), StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_failing_write_without_a_fallback_disables_the_logger_without_throwing()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Information, null, "first");

        using (var blocker = new FileStream(dir.Resolve("logs/updater-20260918.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            blocker.Lock(0, long.MaxValue / 2);
            logger.Write(UpdateLogLevel.Information, null, "second");
            logger.Write(UpdateLogLevel.Information, null, "third");
        }

        Assert.Null(logger.ActiveDirectory);
        Assert.Null(logger.CurrentFilePath);
        Assert.EndsWith("| first", Assert.Single(Lines(dir.Resolve("logs/updater-20260918.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_after_dispose_is_ignored_and_the_file_is_released()
    {
        using var dir = new TempDirectory();
        var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Information, null, "before");
        string path = logger.CurrentFilePath!;

        logger.Dispose();
        logger.Write(UpdateLogLevel.Information, null, "after");
        logger.Dispose();

        Assert.EndsWith("| before", Assert.Single(File.ReadAllLines(path)), StringComparison.Ordinal);   // ReadAllLines 以 FileShare.Read 打开：只有写句柄已释放才能成功
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_fallback_that_also_fails_on_its_first_write_disables_the_logger_without_throwing()
    {
        using var dir = new TempDirectory();
        string fallbackFile = dir.WriteFile("fallback/updater-20260918.log", string.Empty);
        using var logger = new FileLogger(dir.Resolve("primary"), dir.Resolve("fallback"), UpdateLogLevel.Debug, Clock());
        logger.Write(UpdateLogLevel.Information, null, "first");

        // 主文件与回退文件都被锁住：主目录写失败 -> 切到回退目录 -> 切换后记的那条 Warning 也写失败
        using (var primaryBlocker = new FileStream(dir.Resolve("primary/updater-20260918.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var fallbackBlocker = new FileStream(fallbackFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            primaryBlocker.Lock(0, long.MaxValue / 2);
            fallbackBlocker.Lock(0, long.MaxValue / 2);
            logger.Write(UpdateLogLevel.Information, null, "second");
            logger.Write(UpdateLogLevel.Information, null, "third");
        }

        Assert.Null(logger.ActiveDirectory);
        Assert.Null(logger.CurrentFilePath);
        Assert.EndsWith("| first", Assert.Single(Lines(dir.Resolve("primary/updater-20260918.log"))), StringComparison.Ordinal);
        Assert.Empty(Lines(fallbackFile));
    }

    [Fact]
    [SupportedOSPlatform("windows")]   // 依赖 AppendData 句柄的 OS 级追加语义（Windows）
    public void Two_loggers_appending_to_the_same_file_do_not_overwrite_each_other()
    {
        using var dir = new TempDirectory();
        using var a = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        using var b = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        a.Write(UpdateLogLevel.Information, null, "A1");
        b.Write(UpdateLogLevel.Information, null, "B1");
        a.Write(UpdateLogLevel.Information, null, "A2");
        b.Write(UpdateLogLevel.Information, null, "B2");
        a.Write(UpdateLogLevel.Information, null, "A3");

        string[] messages = [.. Lines(a.CurrentFilePath!).Select(l => l.Split(" | ")[^1])];
        Assert.Equal(new[] { "A1", "B1", "A2", "B2", "A3" }, messages);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Two_loggers_writing_concurrently_to_the_same_file_lose_no_lines()
    {
        const int perLogger = 500;
        using var dir = new TempDirectory();
        using var a = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        using var b = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());
        using var barrier = new Barrier(2);

        // 裸线程里没被接住的异常会杀掉整个测试宿主进程：收集起来，在测试线程上抛出，只让这一个测试失败
        var writerFailures = new ConcurrentBag<Exception>();
        Thread[] writers = [.. new[] { a, b }.Select((logger, index) => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (int i = 0; i < perLogger; i++)
                {
                    logger.Write(UpdateLogLevel.Information, null, $"logger-{index} line {i}");
                }
            }
            catch (Exception ex)
            {
                writerFailures.Add(ex);
            }
        }))];
        foreach (Thread writer in writers)
        {
            writer.Start();
        }

        foreach (Thread writer in writers)
        {
            writer.Join();
        }

        if (!writerFailures.IsEmpty)
        {
            throw new AggregateException("写入线程抛出了异常", writerFailures);
        }

        string[] lines = Lines(a.CurrentFilePath!);
        Assert.Equal(2 * perLogger, lines.Length);
        Assert.All(lines, l => Assert.Matches(new Regex(@" \| INFO \| - \| logger-[01] line \d+$"), l));
        Assert.Equal(perLogger, lines.Count(l => l.Contains("logger-0 line ", StringComparison.Ordinal)));
        Assert.Equal(perLogger, lines.Count(l => l.Contains("logger-1 line ", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_exception_whose_ToString_throws_is_logged_by_type_name_instead_of_failing_the_write()
    {
        using var dir = new TempDirectory();
        using var logger = new FileLogger(dir.Resolve("logs"), null, UpdateLogLevel.Debug, Clock());

        logger.Write(UpdateLogLevel.Error, UpdateStage.Commit, "failed", new BadToStringException());
        logger.Write(UpdateLogLevel.Information, null, "next entry");

        string[] lines = Lines(logger.CurrentFilePath!);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("| failed", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("    " + typeof(BadToStringException).FullName + " (ToString", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("| next entry", lines[2], StringComparison.Ordinal);
    }

    private sealed class BadToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("bad ToString");
    }
}
