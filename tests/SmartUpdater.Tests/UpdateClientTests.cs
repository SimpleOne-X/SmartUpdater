using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateClientTests : IDisposable
{
    private static readonly byte[] PackageBytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 97)).ToArray();

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class Harness
    {
        public Harness(TempDirectory temp)
        {
            Engine = new FakeUpdateEngine(installDirectory: temp.Resolve("app"), localAppDataDirectory: temp.Resolve("appdata"));
            Directory.CreateDirectory(temp.Resolve("app"));
            Downloader = new FakePackageDownloader
            {
                Behavior = (release, destination, progress, _) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, PackageBytes);
                    progress?.Report(new DownloadProgress(release.Package.Size, release.Package.Size, 1));
                    return Task.FromResult(destination);
                },
            };
        }

        public FakeUpdateEngine Engine { get; }
        public FakePackageDownloader Downloader { get; }
        public FakeReleaseFeed Feed { get; set; } = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.3")), "\"e1\"");
        public RecordingReporter Reporter { get; } = new();
        public FakeProcessLauncher Launcher { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public RecordingSynchronizationContext? Context { get; set; }
        public UpdateClientOptions Options { get; set; } = new() { FeedUrl = "https://server/updates/releases.json", ReportUrl = "https://server/api/v1/update-reports", JitterWindow = TimeSpan.Zero };

        public UpdateEnvironment Env => new()
        {
            ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
            CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe"],
            EntryAssemblyName = "MyApp",
            EntryAssemblyVersion = new Version(1, 2, 3, 0),
            InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
            LocalApplicationDataDirectory = @"C:\Users\x\AppData\Local",
            ProcessId = 1234,
            TimeProvider = Time,
            Random = new Random(1),
            ProcessLauncher = Launcher,
            CaptureSynchronizationContext = () => Context,
        };

        public UpdateClient Client() => new(Options, Env, Engine, new TransportSet(Feed, Downloader, Reporter, null));
    }

    private static ReleaseEntry Release(string version, UpdateMode mode = UpdateMode.Mandatory)
        => FeedFixtures.Release(version, mode, size: PackageBytes.Length, sha256: FeedFixtures.Sha256Hex(PackageBytes));

    [Fact]
    public async Task RunAsync_completes_normally_after_the_new_process_is_launched()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), "\"e1\"") };
        using UpdateClient client = h.Client();
        var restarting = new List<Version>();
        var progress = new List<UpdateStage>();
        client.Restarting += (_, e) => { restarting.Add(e.Version); e.WaitFor(Task.CompletedTask); };
        client.ProgressChanged += (_, e) => progress.Add(e.Stage);

        await client.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([new Version(1, 2, 4, 0)], restarting);
        Launch launch = Assert.Single(h.Launcher.Launches);
        Assert.Equal("--smartupdater-updated 1.2.4.0 --smartupdater-wait-pid 1234", launch.Arguments);
        Assert.Contains(UpdateStage.Download, progress);
        Assert.Contains(UpdateStage.Commit, progress);
        Assert.Equal(new Version(1, 2, 4, 0), h.Engine.State.CurrentVersion);
        Assert.Empty(h.Engine.Queued);        // 这一轮没有入队任何上报
    }

    [Fact]
    public async Task Events_are_marshalled_to_the_captured_synchronization_context()
    {
        var ctx = new RecordingSynchronizationContext();
        var h = new Harness(_temp) { Context = ctx, Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), "\"e1\"") };
        using UpdateClient client = h.Client();
        SynchronizationContext? seenOnRestarting = null;
        client.Restarting += (_, _) => seenOnRestarting = SynchronizationContext.Current;
        client.ProgressChanged += (_, _) => { };

        Task run = Task.Run(() => client.RunAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await ctx.DrainUntilAsync(run);

        Assert.Same(ctx, seenOnRestarting);
        Assert.True(ctx.PostCount >= 2);       // 至少 Restarting + 进度
        Assert.Single(h.Launcher.Launches);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_update()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), "\"e1\"") };
        using UpdateClient client = h.Client();
        client.Restarting += (_, _) => throw new InvalidOperationException("consumer bug");
        client.ProgressChanged += (_, _) => throw new InvalidOperationException("progress bug");

        await client.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(h.Launcher.Launches);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error), e => e.Exception?.Message == "consumer bug");
    }

    [Fact]
    public async Task Launch_failure_raises_Failed_Commit_reports_it_and_keeps_running()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), "\"e1\"") };
        h.Launcher.ThrowOnStart = new InvalidOperationException("no exe");
        using UpdateClient client = h.Client();
        var failed = new List<UpdateFailedEventArgs>();
        client.Failed += (_, e) => failed.Add(e);
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Reporter.WaitForAsync(2);                       // Failed(Commit) + Heartbeat
        Assert.False(run.IsCompleted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        UpdateFailedEventArgs e = Assert.Single(failed);
        Assert.Equal(UpdateStage.Commit, e.Stage);
        Assert.Equal(new Version(1, 2, 4, 0), e.Version);
        Assert.Contains(h.Reporter.Received, r => r.EventType == UpdateEventType.Failed && r.Stage == UpdateStage.Commit && r.ToVersion == new Version(1, 2, 4, 0));
    }

    [Fact]
    public async Task Polls_again_after_the_feed_supplied_interval()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(new ClientPolicy { PollIntervalSeconds = 120 }, Release("1.2.3")), "\"e1\"") };
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);
        Assert.Single(h.Feed.RequestedETags);
        Assert.Equal(TimeSpan.FromSeconds(120), client.CurrentSettings.PollInterval);
        h.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Single(h.Feed.RequestedETags);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Time.WaitForTimersAsync(1);
        Assert.Equal(2, h.Feed.RequestedETags.Count);
        Assert.Equal("\"e1\"", h.Feed.RequestedETags[1]);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Heartbeat_is_sent_immediately_on_first_run_then_every_interval()
    {
        var h = new Harness(_temp);
        h.Options = new UpdateClientOptions { Feed = h.Feed, Reporter = h.Reporter, HeartbeatInterval = TimeSpan.FromMinutes(10), PollInterval = TimeSpan.FromMinutes(1), JitterWindow = TimeSpan.Zero };
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Reporter.WaitForAsync(1);
        UpdateReport first = Assert.Single(h.Reporter.Received);
        Assert.Equal(UpdateEventType.Heartbeat, first.EventType);
        Assert.Equal(new Version(1, 2, 3, 0), first.ToVersion);
        Assert.Equal(h.Time.GetUtcNow(), h.Engine.State.LastReportedAt);

        await h.Time.WaitForTimersAsync(1);
        h.Time.Advance(TimeSpan.FromMinutes(9));               // 9 次轮询，仍不到心跳间隔
        await h.Time.WaitForTimersAsync(1);
        Assert.Single(h.Reporter.Received);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Reporter.WaitForAsync(2);
        Assert.Equal(2, h.Reporter.Received.Count);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Heartbeat_respects_the_persisted_last_report_time()
    {
        var h = new Harness(_temp);
        h.Engine.State.LastReportedAt = h.Time.GetUtcNow() - TimeSpan.FromHours(1);        // 缺省间隔 6 h，还没到
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);
        Assert.Empty(h.Reporter.Received);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Failed_reports_are_flushed_in_the_same_iteration()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Throwing(new HttpRequestException("boom")) };
        h.Engine.State.LastReportedAt = h.Time.GetUtcNow();
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();
        var failed = new List<UpdateFailedEventArgs>();
        client.Failed += (_, e) => failed.Add(e);

        Task run = client.RunAsync(cts.Token);
        await h.Reporter.WaitForAsync(1);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(UpdateStage.Check, Assert.Single(failed).Stage);
        Assert.Equal(UpdateStage.Check, Assert.Single(h.Reporter.Received).Stage);
    }

    [Fact]
    public async Task Reporter_failures_do_not_stop_the_loop()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Throwing(new HttpRequestException("boom")) };
        h.Reporter.Accept = _ => throw new IOException("network down");
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);          // 已进入下一轮等待
        Assert.NotEmpty(h.Engine.Queued);            // 留在队列里
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Exception is IOException);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Unwritable_install_directory_disables_checks_but_keeps_heartbeats()
    {
        var h = new Harness(_temp);
        h.Engine.ProbeResult = new InstallDirectoryProbeResult(false, "拒绝访问");
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Reporter.WaitForAsync(1);
        Assert.Empty(h.Feed.RequestedETags);
        Assert.Equal(UpdateEventType.Heartbeat, Assert.Single(h.Reporter.Received).EventType);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error), e => e.Message.Contains("拒绝访问", StringComparison.Ordinal));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Without_a_reporter_nothing_is_queued()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Throwing(new HttpRequestException("boom")) };
        h.Options = new UpdateClientOptions { Feed = h.Feed, JitterWindow = TimeSpan.Zero };
        using var client = new UpdateClient(h.Options, h.Env, h.Engine, new TransportSet(h.Feed, h.Downloader, null, null));
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.DoesNotContain(h.Engine.Queued, r => r.EventType == UpdateEventType.Heartbeat);
    }

    [Fact]
    public async Task Concurrent_RunAsync_is_rejected_and_the_flag_is_released_after_cancellation()
    {
        var h = new Harness(_temp);
        using UpdateClient client = h.Client();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(cts.Token));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        using var again = new CancellationTokenSource();
        Task second = client.RunAsync(again.Token);
        await h.Time.WaitForTimersAsync(1);
        again.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public void Dispose_releases_the_engine()
    {
        var h = new Harness(_temp);
        UpdateClient client = h.Client();

        client.Dispose();
        client.Dispose();

        Assert.True(h.Engine.IsDisposed);
    }

    [Fact]
    public void Options_are_validated_in_the_constructor()
    {
        var h = new Harness(_temp);

        Assert.Throws<ArgumentException>(() => new UpdateClient(new UpdateClientOptions(), h.Env, h.Engine, null));
        Assert.Throws<ArgumentNullException>(() => new UpdateClient((UpdateClientOptions)null!));
        Assert.ThrowsAny<ArgumentException>(() => new UpdateClient("not a url"));
    }

    [Theory]
    [InlineData(null, null, "Derived-00000000", "Derived-00000000")]
    [InlineData(null, "FromRun-00000000", "Derived-00000000", "FromRun-00000000")]
    [InlineData("Explicit", null, "Derived-00000000", "Explicit")]
    [InlineData("Explicit", "Explicit", "Derived-00000000", "Explicit")]
    public void Effective_app_id_prefers_options_then_Run_then_derivation(string? options, string? lastRun, string derived, string expected)
    {
        Assert.Equal(expected, UpdateClient.ResolveEffectiveAppId(options, lastRun, derived));
    }

    [Fact]
    public void Conflicting_app_ids_throw()
    {
        Assert.Throws<InvalidOperationException>(() => UpdateClient.ResolveEffectiveAppId("A", "B", "D"));
    }

    [Fact]
    public void Public_constructor_with_custom_feed_and_file_logging_off_does_not_touch_the_disk()
    {
        using var client = new UpdateClient(new UpdateClientOptions { Feed = new FakeReleaseFeed(), Downloader = new FakePackageDownloader(), EnableFileLogging = false });

        Assert.NotNull(client);
    }

    [Fact]
    public async Task UpdateAvailable_subscription_is_what_enables_the_prompt()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4", UpdateMode.Optional)), "\"e1\"") };
        using UpdateClient client = h.Client();
        var seen = new List<UpdateAvailableEventArgs>();
        client.UpdateAvailable += (_, e) => { seen.Add(e); e.Skip(); };
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(cts.Token);
        await h.Time.WaitForTimersAsync(1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        UpdateAvailableEventArgs e = Assert.Single(seen);
        Assert.Equal(new Version(1, 2, 4, 0), e.Version);
        Assert.Contains(new Version(1, 2, 4, 0), h.Engine.State.SkippedVersions);
        Assert.Empty(h.Launcher.Launches);
    }
}
