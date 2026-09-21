using System.Net;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateCycleTests : IDisposable
{
    private static readonly byte[] PackageBytes = Enumerable.Range(0, 5000).Select(i => (byte)(i % 199)).ToArray();

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
                    progress?.Report(new DownloadProgress(release.Package.Size, release.Package.Size, 2048));
                    return Task.FromResult(destination);
                },
            };
        }

        public FakeUpdateEngine Engine { get; }
        public FakePackageDownloader Downloader { get; }
        public RecordingEventSink Events { get; } = new() { HasUpdateAvailableSubscribers = false };
        public ManualTimeProvider Time { get; } = new();
        public Random Random { get; } = new(1);
        public FakeReleaseFeed Feed { get; set; } = new();
        public UpdateClientOptions Options { get; set; } = new() { FeedUrl = "https://server/updates/releases.json", JitterWindow = TimeSpan.Zero };

        public UpdateEnvironment Env => new()
        {
            ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
            CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe"],
            EntryAssemblyName = "MyApp",
            EntryAssemblyVersion = new Version(1, 2, 3, 0),
            InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
            TimeProvider = Time,
            Random = Random,
            MachineName = "PC-01",
        };

        public UpdateCycle Cycle() => new(ResolvedClientOptions.From(Options, Env), Feed, Downloader, Engine, Events, Env);
    }

    private static ReleaseEntry Release(string version, UpdateMode mode = UpdateMode.Mandatory, string? minUpdatableFrom = null, int rollout = 100)
        => FeedFixtures.Release(version, mode, minUpdatableFrom, rollout, size: PackageBytes.Length, sha256: FeedFixtures.Sha256Hex(PackageBytes));

    private Harness Ready(params ReleaseEntry[] releases)
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(releases), "\"e1\"") };
        return h;
    }

    // ---------------------------------------------------------------- 主路径

    [Fact]
    public async Task Mandatory_update_runs_download_verify_checks_and_apply_then_reports_Installed()
    {
        Harness h = Ready(Release("1.2.4"), Release("1.2.3"));

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        Assert.Equal(new Version(1, 2, 4, 0), outcome.TargetVersion);
        (ReleaseEntry release, string destination) = Assert.Single(h.Downloader.Calls);
        Assert.Equal(new Version(1, 2, 4), release.Version);                                       // 下载器收到原始条目
        Assert.Equal(h.Engine.Layout.GetDownloadPath(new Version(1, 2, 4, 0)), destination);      // 缓存文件名用四段
        (string appliedPath, Version appliedCurrent) = Assert.Single(h.Engine.ApplyCalls);
        Assert.Equal(destination, appliedPath);
        Assert.Equal(new Version(1, 2, 3, 0), appliedCurrent);
        Assert.Equal(new Version(1, 2, 4, 0), h.Engine.State.CurrentVersion);
        Assert.Equal(1, h.Engine.State.ChainLength);
        Assert.Equal("\"e1\"", h.Engine.State.FeedETag);
        Assert.Equal(h.Time.GetUtcNow(), h.Engine.State.LastCheckedAt);
        Assert.Empty(h.Events.Failed);
        Assert.Empty(h.Engine.Queued);
        Assert.Contains(h.Events.Progress, p => p.Stage == UpdateStage.Download && p.Percent == 100);
        Assert.Contains(h.Events.Progress, p => p.Stage == UpdateStage.Verify);
        Assert.Contains(h.Events.Progress, p => p.Stage == UpdateStage.Commit);
    }

    [Fact]
    public async Task Check_log_lists_current_top_outcome_and_every_verdict_with_bucket_and_percent()
    {
        Harness h = Ready(Release("2.0", minUpdatableFrom: "1.9"), Release("1.5", rollout: 0), Release("1.2.4"));

        await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.True(h.Engine.RecordingLog.Contains("当前 1.2.3.0"));
        Assert.True(h.Engine.RecordingLog.Contains("feed 最高 2.0.0.0"));
        Assert.True(h.Engine.RecordingLog.Contains("BelowFloor"));
        Assert.True(h.Engine.RecordingLog.Contains("OutOfRollout"));
        Assert.True(h.Engine.RecordingLog.Contains("rolloutPercent 0"));
        Assert.True(h.Engine.RecordingLog.Contains("灰度桶"));
        Assert.True(h.Engine.RecordingLog.Contains("Selected"));
    }

    [Fact]
    public async Task No_update_resets_chain_length_and_returns_NoUpdate()
    {
        Harness h = Ready(Release("1.2.3"));
        h.Engine.State.ChainLength = 3;

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.NoUpdate, outcome.Kind);
        Assert.Null(outcome.TargetVersion);
        Assert.Equal(0, h.Engine.State.ChainLength);
        Assert.Empty(h.Downloader.Calls);
    }

    [Fact]
    public async Task Feed_client_section_overrides_local_settings_with_floor_protection()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(new ClientPolicy { PollIntervalSeconds = 1, JitterWindowSeconds = 120, HeartbeatIntervalSeconds = 7200 }, Release("1.2.3"))) };
        UpdateCycle cycle = h.Cycle();
        Assert.Equal(ClientPolicyLimits.Defaults.PollInterval, cycle.Settings.PollInterval);

        CycleOutcome outcome = await cycle.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClientPolicyLimits.MinPollInterval, outcome.Settings.PollInterval);        // 1 s 被钳到 60 s
        Assert.Equal(TimeSpan.FromSeconds(120), outcome.Settings.JitterWindow);
        Assert.Equal(TimeSpan.FromSeconds(7200), outcome.Settings.HeartbeatInterval);
        Assert.Equal(outcome.Settings, cycle.Settings);
    }

    // ---------------------------------------------------------------- 304 与 ETag

    [Fact]
    public async Task Not_modified_reuses_the_cached_document()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.WithETag(FeedFixtures.Feed(Release("1.2.3")), "\"e1\"") };
        UpdateCycle cycle = h.Cycle();

        CycleOutcome first = await cycle.RunAsync(TestContext.Current.CancellationToken);
        CycleOutcome second = await cycle.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.NoUpdate, first.Kind);
        Assert.Equal(CycleOutcomeKind.NoUpdate, second.Kind);
        Assert.Equal([null, "\"e1\""], h.Feed.RequestedETags);
        Assert.True(h.Engine.RecordingLog.Contains("304"));
    }

    [Fact]
    public async Task Not_modified_without_a_cached_document_refetches_unconditionally()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.WithETag(FeedFixtures.Feed(Release("1.2.3")), "\"e1\"") };
        h.Engine.State.FeedETag = "\"e1\"";          // 上次进程留下的 ETag，本进程还没有文档

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.NoUpdate, outcome.Kind);
        Assert.Equal(["\"e1\"", null], h.Feed.RequestedETags);
    }

    [Fact]
    public async Task Not_modified_on_an_unconditional_request_is_a_Check_failure()
    {
        var h = new Harness(_temp) { Feed = new FakeReleaseFeed() };     // 永远 304

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(UpdateStage.Check, outcome.Failure!.Stage);
        Assert.Equal(UpdateStage.Check, Assert.Single(h.Events.Failed).Stage);
    }

    // ---------------------------------------------------------------- Check 阶段失败

    [Fact]
    public async Task Feed_transport_failure_is_reported_as_Check_with_log_tail()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Throwing(new HttpRequestException("404", null, HttpStatusCode.NotFound)) };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Failed, outcome.Kind);
        UpdateFailedEventArgs failed = Assert.Single(h.Events.Failed);
        Assert.Equal(UpdateStage.Check, failed.Stage);
        Assert.Null(failed.Version);
        UpdateReport report = Assert.Single(h.Engine.Queued);
        Assert.Equal(UpdateEventType.Failed, report.EventType);
        Assert.Equal(UpdateStage.Check, report.Stage);
        Assert.Equal(new Version(1, 2, 3, 0), report.FromVersion);
        Assert.Null(report.ToVersion);
        Assert.Equal("last log lines", report.LogTail);
        Assert.Equal(FakeUpdateEngine.DeviceGuid, report.DeviceGuid);
        Assert.False(report.IsSuccess);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error), e => e.Exception is HttpRequestException);
    }

    [Fact]
    public async Task Log_tail_is_omitted_when_the_option_is_off()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Throwing(new HttpRequestException("x")) };
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", IncludeLogTailOnFailure = false };

        await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(h.Engine.Queued).LogTail);
    }

    [Fact]
    public async Task Rejected_feed_is_a_Check_failure_with_the_reason()
    {
        var doc = new ReleaseFeedDocument { SchemaVersion = 2, Releases = [Release("1.2.4")] };
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(doc) };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Check, outcome.Failure!.Stage);
        Assert.Contains("schemaVersion", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_document_on_a_modified_result_is_a_Check_failure()
    {
        var h = new Harness(_temp) { Feed = new FakeReleaseFeed { Responder = _ => new FeedResult(false, null, null) } };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Check, outcome.Failure!.Stage);
    }

    // ---------------------------------------------------------------- optional 更新的处置

    [Fact]
    public async Task Optional_with_no_subscribers_is_accepted_automatically_with_jitter()
    {
        Harness h = Ready(Release("1.2.4", UpdateMode.Optional));
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.FromMinutes(10) };
        long expectedTicks = new Random(1).NextInt64(TimeSpan.FromMinutes(10).Ticks);

        Task<CycleOutcome> run = h.Cycle().RunAsync(TestContext.Current.CancellationToken);
        await h.Time.WaitForTimersAsync(1);
        Assert.Empty(h.Downloader.Calls);
        h.Time.Advance(TimeSpan.FromTicks(expectedTicks - 1));
        Assert.Empty(h.Downloader.Calls);
        h.Time.Advance(TimeSpan.FromTicks(1));

        CycleOutcome outcome = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        Assert.Empty(h.Events.Available);
    }

    [Fact]
    public async Task Optional_accepted_by_the_handler_downloads_immediately_without_jitter()
    {
        Harness h = Ready(Release("1.2.4", UpdateMode.Optional, rollout: 100));
        h.Events.HasUpdateAvailableSubscribers = true;
        h.Events.AvailableBehavior = _ => UpdateDecision.Accept;
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.FromMinutes(10) };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        UpdateAvailableEventArgs args = Assert.Single(h.Events.Available);
        Assert.Equal(new Version(1, 2, 4, 0), args.Version);
        Assert.Equal(new Version(1, 2, 3, 0), args.CurrentVersion);
        Assert.Equal(UpdateMode.Optional, args.Mode);
        Assert.Equal(PackageBytes.Length, args.PackageSize);
        Assert.Equal(0, h.Time.ActiveTimerCount);
    }

    [Fact]
    public async Task Optional_postponed_is_not_asked_again_in_this_process()
    {
        Harness h = Ready(Release("1.2.4", UpdateMode.Optional));
        h.Events.HasUpdateAvailableSubscribers = true;
        h.Events.AvailableBehavior = _ => UpdateDecision.Postpone;
        UpdateCycle cycle = h.Cycle();

        CycleOutcome first = await cycle.RunAsync(TestContext.Current.CancellationToken);
        CycleOutcome second = await cycle.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Postponed, first.Kind);
        Assert.Equal(CycleOutcomeKind.Postponed, second.Kind);
        Assert.Single(h.Events.Available);
        Assert.Contains(new Version(1, 2, 4, 0), cycle.PostponedVersions);
        Assert.Empty(h.Downloader.Calls);
        Assert.Empty(h.Engine.State.SkippedVersions);
    }

    [Fact]
    public async Task Optional_skipped_is_persisted_and_excluded_next_time()
    {
        Harness h = Ready(Release("1.2.4", UpdateMode.Optional), Release("1.2.3"));
        h.Events.HasUpdateAvailableSubscribers = true;
        h.Events.AvailableBehavior = _ => UpdateDecision.Skip;
        UpdateCycle cycle = h.Cycle();

        CycleOutcome first = await cycle.RunAsync(TestContext.Current.CancellationToken);
        CycleOutcome second = await cycle.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Skipped, first.Kind);
        Assert.Contains(new Version(1, 2, 4, 0), h.Engine.State.SkippedVersions);
        Assert.Equal(CycleOutcomeKind.NoUpdate, second.Kind);
        Assert.Single(h.Events.Available);
    }

    [Fact]
    public async Task Optional_without_a_decision_is_treated_as_postponed_with_a_warning()
    {
        Harness h = Ready(Release("1.2.4", UpdateMode.Optional));
        h.Events.HasUpdateAvailableSubscribers = true;
        h.Events.AvailableBehavior = _ => null;

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Postponed, outcome.Kind);
        Assert.NotEmpty(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning));
    }

    [Fact]
    public async Task Mandatory_never_raises_UpdateAvailable()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Events.HasUpdateAvailableSubscribers = true;
        h.Events.AvailableBehavior = _ => UpdateDecision.Skip;

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        Assert.Empty(h.Events.Available);
    }

    // ---------------------------------------------------------------- 各阶段失败

    [Fact]
    public async Task Download_failure_is_reported_as_Download()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Downloader.Behavior = (_, _, _, _) => Task.FromException<string>(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable));

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Download, outcome.Failure!.Stage);
        Assert.Equal(new Version(1, 2, 4, 0), Assert.Single(h.Events.Failed).Version);
        Assert.Equal(new Version(1, 2, 4, 0), Assert.Single(h.Engine.Queued).ToVersion);
        Assert.Empty(h.Engine.ApplyCalls);
    }

    [Fact]
    public async Task Hash_mismatch_is_reported_as_Verify_and_the_file_is_deleted()
    {
        Harness h = Ready(FeedFixtures.Release("1.2.4", size: PackageBytes.Length, sha256: FeedFixtures.Sha256Hex([9, 9, 9])));

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Verify, outcome.Failure!.Stage);
        Assert.False(File.Exists(h.Downloader.Calls[0].Destination));
        Assert.Empty(h.Engine.ApplyCalls);
    }

    [Fact]
    public async Task Missing_signature_with_a_public_key_is_reported_as_Verify()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Harness h = Ready(Release("1.2.4"));
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.Zero, PublicKey = ReleaseSignature.ExportPublicKey(key) };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Verify, outcome.Failure!.Stage);
        Assert.Contains("签名", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_version_mismatch_is_reported_as_Verify()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.PackageReader = _ => new PackageSummary(new Version(9, 9), 1000);

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Verify, outcome.Failure!.Stage);
        Assert.Empty(h.Engine.ApplyCalls);
    }

    [Fact]
    public async Task Insufficient_disk_is_reported_as_DiskCheck_and_keeps_the_download()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.DiskCheck = (pkg, files) => new DiskSpaceCheckResult(new VolumeCheck(@"C:\", pkg + files, 10), null);

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.DiskCheck, outcome.Failure!.Stage);
        Assert.Contains("10", outcome.Failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(h.Downloader.Calls[0].Destination));
        Assert.Empty(h.Engine.ApplyCalls);
    }

    [Fact]
    public async Task Unwritable_install_directory_is_reported_as_PermissionCheck()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.ProbeResult = new InstallDirectoryProbeResult(false, "拒绝访问");

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.PermissionCheck, outcome.Failure!.Stage);
        Assert.Empty(h.Engine.ApplyCalls);
    }

    [Theory]
    [InlineData(UpdateStage.Commit)]
    [InlineData(UpdateStage.Rollback)]
    public async Task Apply_failure_keeps_its_stage(UpdateStage stage)
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.ApplyBehavior = (_, _, _, _) => throw new UpdateFailedException(stage, "提交中途失败");

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(stage, outcome.Failure!.Stage);
        Assert.Equal(stage, Assert.Single(h.Engine.Queued).Stage);
        Assert.Equal(new Version(1, 2, 3, 0), h.Engine.State.CurrentVersion);
        Assert.Equal(0, h.Engine.State.ChainLength);
    }

    [Fact]
    public async Task Unexpected_exception_is_wrapped_with_the_current_stage()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.ApplyBehavior = (_, _, _, _) => throw new IOException("disk vanished");

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Commit, outcome.Failure!.Stage);
        Assert.IsType<IOException>(outcome.Failure.InnerException);
    }

    [Fact]
    public async Task Cancellation_propagates_without_events_or_reports()
    {
        Harness h = Ready(Release("1.2.4"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cycle().RunAsync(cts.Token));

        Assert.Empty(h.Events.Failed);
        Assert.Empty(h.Engine.Queued);
    }

    // ---------------------------------------------------------------- 跳板熔断

    [Fact]
    public async Task Chain_limit_reports_Check_failure_resets_the_counter_and_remembers_the_feed_etag()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.State.ChainLength = ReleaseSelector.MaxChainLength;

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.ChainLimitReached, outcome.Kind);
        Assert.Equal(UpdateStage.Check, outcome.Failure!.Stage);
        Assert.Contains("5", outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(UpdateStage.Check, Assert.Single(h.Events.Failed).Stage);
        Assert.Single(h.Engine.Queued);
        Assert.Equal(0, h.Engine.State.ChainLength);
        Assert.Equal("\"e1\"", h.Engine.State.ChainLimitFeedETag);
        Assert.Empty(h.Downloader.Calls);
    }

    [Fact]
    public async Task Chain_limit_stays_silent_while_the_feed_is_unchanged_and_lifts_when_it_changes()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.State.ChainLimitFeedETag = "\"e1\"";

        CycleOutcome silent = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CycleOutcomeKind.ChainLimitReached, silent.Kind);
        Assert.Null(silent.Failure);
        Assert.Empty(h.Events.Failed);
        Assert.Empty(h.Engine.Queued);

        h.Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), "\"e2\"");
        CycleOutcome lifted = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CycleOutcomeKind.Installed, lifted.Kind);
        Assert.Null(h.Engine.State.ChainLimitFeedETag);
    }

    [Fact]
    public async Task Chain_limit_without_etag_only_resets_the_counter()
    {
        var h = new Harness(_temp) { Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.2.4")), etag: null) };
        h.Engine.State.ChainLength = 5;

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.ChainLimitReached, outcome.Kind);
        Assert.Null(h.Engine.State.ChainLimitFeedETag);
        Assert.Equal(0, h.Engine.State.ChainLength);
    }

    [Fact]
    public async Task Five_consecutive_installs_then_the_sixth_check_trips_the_limit()
    {
        // 模拟 A→B→A→B… 的环：每次装完（假引擎会把 state.CurrentVersion 改成目标）都换成"另一个版本更高"的 feed
        var h = new Harness(_temp);
        for (int i = 1; i <= 5; i++)
        {
            h.Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release($"1.{i + 3}")), "\"e1\"");
            CycleOutcome installed = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(CycleOutcomeKind.Installed, installed.Kind);
            Assert.Equal(i, h.Engine.State.ChainLength);
        }

        h.Feed = FakeReleaseFeed.Returning(FeedFixtures.Feed(Release("1.9")), "\"e1\"");
        CycleOutcome tripped = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.ChainLimitReached, tripped.Kind);
        Assert.NotNull(tripped.Failure);
    }

    // ---------------------------------------------------------------- 降级、当前版本、跳过集合

    [Fact]
    public async Task Downgrade_only_when_allowed()
    {
        Harness denied = Ready(Release("1.0.0"));
        Assert.Equal(CycleOutcomeKind.NoUpdate, (await denied.Cycle().RunAsync(TestContext.Current.CancellationToken)).Kind);

        Harness allowed = Ready(Release("1.0.0"));
        allowed.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.Zero, AllowVersionDowngrade = true };
        CycleOutcome outcome = await allowed.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        Assert.Equal(new Version(1, 0, 0, 0), outcome.TargetVersion);
    }

    [Fact]
    public async Task Skipped_current_version_does_not_push_a_downgrade()
    {
        Harness h = Ready(Release("1.2.3"), Release("1.2.2"));
        h.Engine.State.SkippedVersions = [new Version(1, 2, 3)];
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.Zero, AllowVersionDowngrade = true };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.NoUpdate, outcome.Kind);
    }

    [Fact]
    public async Task Current_version_falls_back_to_manifest_then_assembly_and_is_persisted()
    {
        Harness fromManifest = Ready(Release("1.2.4"));
        fromManifest.Engine.State.CurrentVersion = null;
        fromManifest.Engine.InstalledVersion = new Version(1, 2, 3);
        await fromManifest.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Version(1, 2, 3, 0), Assert.Single(fromManifest.Engine.ApplyCalls).CurrentVersion);

        Harness fromAssembly = Ready(Release("1.2.4"));
        fromAssembly.Engine.State.CurrentVersion = null;
        fromAssembly.Engine.InstalledVersion = null;
        await fromAssembly.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Version(1, 2, 3, 0), Assert.Single(fromAssembly.Engine.ApplyCalls).CurrentVersion);

        Harness explicitOption = Ready(Release("1.2.4"));
        explicitOption.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.Zero, CurrentVersion = new Version(1, 0) };
        await explicitOption.Cycle().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Version(1, 0, 0, 0), Assert.Single(explicitOption.Engine.ApplyCalls).CurrentVersion);
    }

    [Fact]
    public async Task Failure_report_carries_duration_from_the_injected_clock()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Downloader.Behavior = (_, _, _, _) => { h.Time.Advance(TimeSpan.FromSeconds(2.5)); throw new IOException("x"); };

        await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2500, Assert.Single(h.Engine.Queued).DurationMs);
    }

    [Fact]
    public async Task Null_element_in_the_skipped_list_does_not_break_selection()
    {
        Harness h = Ready(Release("1.2.4"));
        h.Engine.State.SkippedVersions = [null!, new Version(1, 2, 9)];

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
    }

    [Fact]
    public async Task Three_segment_feed_version_is_verified_against_the_raw_entry_signature()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        ReleaseEntry unsigned = Release("1.2.4");
        string signature = ReleaseSignature.Sign(unsigned, key);
        ReleaseEntry signed = FeedFixtures.Release("1.2.4", size: PackageBytes.Length, sha256: FeedFixtures.Sha256Hex(PackageBytes), signature: signature);
        Harness h = Ready(signed);
        h.Options = new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.Zero, PublicKey = ReleaseSignature.ExportPublicKey(key) };

        CycleOutcome outcome = await h.Cycle().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CycleOutcomeKind.Installed, outcome.Kind);
        Assert.Equal(new Version(1, 2, 4), Assert.Single(h.Downloader.Calls).Release.Version);
    }
}
