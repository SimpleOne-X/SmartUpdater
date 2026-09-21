using System.Collections.Concurrent;
using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReportQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly IFileOperations Fs = PhysicalFileOperations.Instance;

    private static UpdateReport Report(string name, DateTimeOffset? at = null, UpdateEventType type = UpdateEventType.Failed) => new()
    {
        EventType = type,
        DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Stage = type == UpdateEventType.Failed ? UpdateStage.Commit : null,
        ErrorMessage = name,
        IsSuccess = type != UpdateEventType.Failed,
        ReportedAt = at ?? Now,
    };

    private static ReportQueue Queue(TempDirectory dir, FakeTimeProvider? clock = null, RecordingLog? log = null, IFileOperations? fs = null)
        => new(dir.Resolve(".smartupdater/reports.jsonl"), fs ?? Fs, clock ?? new FakeTimeProvider(Now), log ?? new RecordingLog());

    private static string[] Names(IReadOnlyList<UpdateReport> reports) => [.. reports.Select(r => r.ErrorMessage!)];

    [Fact]
    public void Enqueue_persists_one_json_line_per_report_oldest_first()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);

        queue.Enqueue(Report("first"));
        queue.Enqueue(Report("second"));

        string[] lines = File.ReadAllLines(dir.Resolve(".smartupdater/reports.jsonl"), Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"errorMessage\":\"first\"", lines[0]);
        Assert.Contains("\"eventType\":\"Failed\"", lines[0]);
        Assert.Contains("\"errorMessage\":\"second\"", lines[1]);
        Assert.Equal(new[] { "first", "second" }, Names(queue.Peek()));
    }

    [Fact]
    public void Log_tail_with_line_breaks_still_occupies_a_single_line()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        var report = new UpdateReport
        {
            EventType = UpdateEventType.Failed,
            DeviceGuid = Guid.NewGuid(),
            ReportedAt = Now,
            LogTail = "line 1\r\nline 2\nline 3",
        };

        queue.Enqueue(report);

        Assert.Single(File.ReadAllLines(dir.Resolve(".smartupdater/reports.jsonl")));
        Assert.Equal("line 1\r\nline 2\nline 3", Assert.Single(queue.Peek()).LogTail);
    }

    [Fact]
    public void Queue_survives_a_new_instance_on_the_same_file()
    {
        using var dir = new TempDirectory();
        Queue(dir).Enqueue(Report("persisted"));

        Assert.Equal(new[] { "persisted" }, Names(Queue(dir).Peek()));
    }

    [Fact]
    public void Corrupt_line_is_skipped_logged_and_dropped_by_the_next_rewrite()
    {
        using var dir = new TempDirectory();
        ReportQueue seed = Queue(dir);
        seed.Enqueue(Report("first"));
        seed.Enqueue(Report("second"));
        string path = dir.Resolve(".smartupdater/reports.jsonl");
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        File.WriteAllText(path, lines[0] + "\n{ this line is broken\n" + lines[1] + "\n", new UTF8Encoding(false));
        var log = new RecordingLog();
        ReportQueue queue = Queue(dir, log: log);

        Assert.Equal(new[] { "first", "second" }, Names(queue.Peek()));
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("2", StringComparison.Ordinal));

        queue.Enqueue(Report("third"));

        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void Load_tolerates_a_utf8_byte_order_mark_on_the_first_line()
    {
        using var dir = new TempDirectory();
        Queue(dir).Enqueue(Report("first"));
        string path = dir.Resolve(".smartupdater/reports.jsonl");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. File.ReadAllBytes(path)]);
        var log = new RecordingLog();

        Assert.Equal(new[] { "first" }, Names(Queue(dir, log: log).Peek()));
        Assert.Empty(log.AtLevel(UpdateLogLevel.Warning));
    }

    [Fact]
    public void Enqueue_drops_expired_entries_from_the_file_itself()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("stale", Now.AddDays(-8)));
        queue.Enqueue(Report("fresh", Now));

        string text = File.ReadAllText(dir.Resolve(".smartupdater/reports.jsonl"));

        Assert.DoesNotContain("stale", text, StringComparison.Ordinal);
        Assert.Contains("fresh", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Oldest_entries_are_evicted_beyond_one_hundred()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        ReportQueue queue = Queue(dir, log: log);

        for (int i = 0; i < ReportQueue.MaxEntries + 1; i++)
        {
            queue.Enqueue(Report($"r{i:000}"));
        }

        IReadOnlyList<UpdateReport> pending = queue.Peek();
        Assert.Equal(ReportQueue.MaxEntries, pending.Count);
        Assert.Equal("r001", pending[0].ErrorMessage);
        Assert.Equal("r100", pending[^1].ErrorMessage);
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("淘汰", StringComparison.Ordinal));
    }

    [Fact]
    public void Entries_older_than_seven_days_are_dropped_on_read()
    {
        using var dir = new TempDirectory();
        var clock = new FakeTimeProvider(Now);
        ReportQueue queue = Queue(dir, clock);
        queue.Enqueue(Report("fresh", Now.AddDays(-6)));
        queue.Enqueue(Report("stale", Now.AddDays(-8)));
        queue.Enqueue(Report("boundary", Now - ReportQueue.MaxAge));

        Assert.Equal(new[] { "fresh", "boundary" }, Names(queue.Peek()));

        clock.Advance(TimeSpan.FromDays(2));

        Assert.Empty(queue.Peek());
    }

    [Fact]
    public async Task Successful_sends_dequeue_oldest_first_and_delete_the_empty_file()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("a"));
        queue.Enqueue(Report("b"));
        var sent = new List<string>();

        int count = await queue.FlushAsync((r, _) =>
        {
            sent.Add(r.ErrorMessage!);
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(new[] { "a", "b" }, sent);
        Assert.Empty(queue.Peek());
        Assert.False(dir.Exists(".smartupdater/reports.jsonl"));
    }

    [Fact]
    public async Task Send_returning_false_keeps_the_entry_and_continues_with_the_next()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("rejected"));
        queue.Enqueue(Report("accepted"));

        int count = await queue.FlushAsync((r, _) => Task.FromResult(r.ErrorMessage == "accepted"), CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(new[] { "rejected" }, Names(queue.Peek()));
    }

    [Fact]
    public async Task Send_throwing_keeps_the_entry_stops_the_pass_and_is_logged()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        ReportQueue queue = Queue(dir, log: log);
        queue.Enqueue(Report("a"));
        queue.Enqueue(Report("b"));
        queue.Enqueue(Report("c"));
        var attempted = new List<string>();

        int count = await queue.FlushAsync((r, _) =>
        {
            attempted.Add(r.ErrorMessage!);
            return r.ErrorMessage == "b" ? throw new HttpRequestException("offline") : Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(new[] { "a", "b" }, attempted);
        Assert.Equal(new[] { "b", "c" }, Names(queue.Peek()));
        Assert.Equal(2, File.ReadAllLines(dir.Resolve(".smartupdater/reports.jsonl")).Length);   // a 已在成功后立即从文件移除
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Exception is HttpRequestException);
    }

    [Fact]
    public async Task A_successful_send_is_persisted_before_the_next_send_starts()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("a"));
        queue.Enqueue(Report("b"));
        var seen = new List<string>();

        await queue.FlushAsync((_, _) =>
        {
            seen.Add(string.Join(",", Names(queue.Peek())));   // send 期间不持锁，Peek 安全；看到的是磁盘上此刻的队列
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Equal(new[] { "a,b", "b" }, seen);
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_the_queue_intact()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("a"));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.FlushAsync((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }, cts.Token));

        Assert.Equal(new[] { "a" }, Names(queue.Peek()));
    }

    [Fact]
    public async Task Enqueue_while_a_send_is_in_flight_is_not_blocked_and_is_preserved()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        queue.Enqueue(Report("in-flight"));
        var sendStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource<bool>();

        Task<int> flush = queue.FlushAsync(async (_, _) =>
        {
            sendStarted.SetResult();
            return await release.Task;
        }, CancellationToken.None);

        await sendStarted.Task;
        queue.Enqueue(Report("arrived-during-send"));          // 必须不被 send 卡住
        release.SetResult(true);

        Assert.Equal(1, await flush);
        Assert.Equal(new[] { "arrived-during-send" }, Names(queue.Peek()));
    }

    [Fact]
    public void Concurrent_enqueues_produce_exactly_n_intact_records()
    {
        using var dir = new TempDirectory();
        ReportQueue queue = Queue(dir);
        const int threads = 4;
        const int perThread = 20;
        using var barrier = new Barrier(threads);
        // 裸线程里没被接住的异常会直接杀掉整个测试宿主进程；这里把它们收集起来，在测试线程上抛出，只让这一个测试失败。
        var workerFailures = new ConcurrentBag<Exception>();
        Thread[] workers = [.. Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (int i = 0; i < perThread; i++)
                {
                    queue.Enqueue(Report($"t{t}-{i}"));
                }
            }
            catch (Exception ex)
            {
                workerFailures.Add(ex);
            }
        }))];

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        if (!workerFailures.IsEmpty)
        {
            throw new AggregateException("工作线程抛出了异常", workerFailures);
        }

        IReadOnlyList<UpdateReport> pending = queue.Peek();
        Assert.Equal(threads * perThread, pending.Count);
        Assert.Equal(threads * perThread, pending.Select(r => r.ErrorMessage).Distinct().Count());
        Assert.Equal(threads * perThread, File.ReadAllLines(dir.Resolve(".smartupdater/reports.jsonl")).Length);
    }

    [Fact]
    public void Peek_on_missing_file_is_empty_and_write_failures_propagate()
    {
        using var dir = new TempDirectory();
        var fs = new FaultInjectingFileOperations(Fs)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenWrite", AtomicFile.TempSuffix),
        };
        ReportQueue queue = Queue(dir, fs: fs);

        Assert.Empty(queue.Peek());
        Assert.Throws<IOException>(() => queue.Enqueue(Report("x")));
    }
}
