using System.Net;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ClientEndToEndTests
{
    private sealed class World : IDisposable
    {
        public World(bool corruptSha = false)
        {
            Fixture = InstallationFixture.StandardV1();
            Server = new LoopbackHttpServer();
            Time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

            string zipPath = InstallationFixture.StandardV110().Save(Fixture.Temp.Resolve("server/packages/App-1.1.0.zip"));
            byte[] zip = File.ReadAllBytes(zipPath);
            var feed = new ReleaseFeedDocument
            {
                SchemaVersion = 1,
                Channel = "stable",
                Client = new ClientPolicy { PollIntervalSeconds = 120 },
                Releases =
                [
                    new ReleaseEntry
                    {
                        Version = new Version(1, 1, 0),
                        ReleasedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
                        Package = new PackageInfo { Url = "packages/App-1.1.0.zip", Size = zip.Length, Sha256 = corruptSha ? FeedFixtures.Sha256Hex([1, 2, 3]) : FeedFixtures.Sha256Hex(zip) },
                        Mode = UpdateMode.Mandatory,
                        Notes = "端到端",
                    },
                ],
            };
            Server.ServeJson("/updates/releases.json", JsonSerializer.Serialize(feed, SmartUpdaterJsonContext.Default.ReleaseFeedDocument), etag: "\"f1\"");
            Server.ServeBytes("/updates/packages/App-1.1.0.zip", zip, etag: "\"p1\"");
            // 请求体已被 LoopbackHttpServer 记录进 Requests（处理器再读 InputStream 只会得到空），这里只回 202；上报内容由 Reports 从记录里解析。
            Server.Map("/api/v1/update-reports", (request, response) =>
            {
                response.StatusCode = (int)HttpStatusCode.Accepted;
                response.Close();
                return Task.CompletedTask;
            });
        }

        public InstallationFixture Fixture { get; }
        public LoopbackHttpServer Server { get; }
        public ManualTimeProvider Time { get; }
        public FakeProcessLauncher Launcher { get; } = new();
        public FakeProcessWaiter Waiter { get; } = new();
        public List<UpdateReport> Reports
            => Server.SnapshotRequests()
                .Where(r => r.Uri.AbsolutePath == "/api/v1/update-reports" && r.Body is not null)
                .Select(r => JsonSerializer.Deserialize(r.Body!, SmartUpdaterJsonContext.Default.UpdateReport)!)
                .ToList();

        public UpdateEnvironment Env(string[]? commandLine = null) => new()
        {
            InstallDirectory = Fixture.Layout.InstallDirectory,
            LocalApplicationDataDirectory = Fixture.Temp.Resolve("appdata"),
            ProcessPath = Fixture.Resolve("app.exe"),
            CommandLineArguments = commandLine ?? [Fixture.Resolve("app.exe")],
            EntryAssemblyName = "TestApp",
            EntryAssemblyVersion = new Version(1, 0, 0, 0),
            ProcessId = 1234,
            MachineName = "PC-E2E",
            TimeProvider = Time,
            Random = new Random(3),
            ProcessLauncher = Launcher,
            ProcessWaiter = Waiter,
        };

        public UpdateClientOptions Options => new()
        {
            FeedUrl = new Uri(Server.BaseUri, "updates/releases.json").ToString(),
            ReportUrl = new Uri(Server.BaseUri, "api/v1/update-reports").ToString(),
            AppId = InstallationFixture.AppId,
            JitterWindow = TimeSpan.Zero,
            EnableFileLogging = true,
        };

        public UpdateClient Client() => new(Options, Env());

        public StartupRunner Runner(string[] commandLine) => new(Env(commandLine), InstallationFixture.AppId, layout =>
        {
            (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(layout, enableFileLogging: true, null, Time);
            return new UpdateEngine(layout, "app.exe", Env(commandLine), log, fileLogger);
        });

        public Task WaitForReportsAsync(int count) => WaitUntilAsync(() => Reports.Count >= count);

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) { throw new TimeoutException("条件在 15 s 内未满足"); }
                await Task.Delay(20);
            }
        }

        public string LogText()
            => string.Join("\n", Directory.EnumerateFiles(Fixture.Layout.LogDirectory, "updater-*.log").Select(p => File.ReadAllText(p)));

        public void Dispose()
        {
            Server.Dispose();
            Fixture.Dispose();
        }
    }

    [Fact]
    public async Task Full_round_trip_from_check_to_restart_and_next_startup()
    {
        using var world = new World();
        var progress = new List<UpdateStage>();
        var restarting = new List<Version>();

        // ---- 第一进程：检测 → 下载 → 校验 → 提交 → Restarting → "启动新进程" → RunAsync 结束
        using (UpdateClient client = world.Client())
        {
            client.ProgressChanged += (_, e) => progress.Add(e.Stage);
            client.Restarting += (_, e) => { restarting.Add(e.Version); e.WaitFor(Task.CompletedTask); };
            client.Failed += (_, e) => Assert.Fail($"不应失败：{e.Stage} {e.Exception}");

            await client.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Equal([new Version(1, 1, 0, 0)], restarting);
        Launch launch = Assert.Single(world.Launcher.Launches);
        Assert.Equal(world.Fixture.Resolve("app.exe"), launch.FileName);
        Assert.Equal("--smartupdater-updated 1.1.0.0 --smartupdater-wait-pid 1234", launch.Arguments);
        Assert.Equal(world.Fixture.Layout.InstallDirectory, launch.WorkingDirectory);
        Assert.Contains(UpdateStage.Download, progress);
        Assert.Contains(UpdateStage.Verify, progress);
        Assert.Contains(UpdateStage.Commit, progress);
        world.Fixture.AssertStandardV110Files();
        Assert.NotEmpty(world.Fixture.Leftovers());                                          // .suold 还在：等新版本启动后清理
        Assert.Equal(JournalState.Done, world.Fixture.ReadJournal().Journal!.State);
        Assert.Equal(new Version(1, 1, 0), world.Fixture.ReadState().CurrentVersion);
        Assert.Equal(1, world.Fixture.ReadState().ChainLength);
        Assert.Equal("\"f1\"", world.Fixture.ReadState().FeedETag);
        Assert.Equal(["1.1.0.0.zip"], world.Fixture.CacheFiles());
        Assert.Equal(1, world.Server.RequestCount("/updates/releases.json"));
        Assert.Equal(1, world.Server.RequestCount("/updates/packages/App-1.1.0.zip"));
        Assert.DoesNotContain(world.Reports, r => r.EventType == UpdateEventType.Failed);

        // ---- 第二进程：SmartUpdaterApp.Run 的内核，用启动器记录的命令行
        string[] args = CommandLineTestHelper.SplitLikeWindows(launch.Arguments);
        StartupResult startup = world.Runner([launch.FileName, .. args]).Run(args);

        Assert.True(startup.JustUpdated);
        Assert.Equal(new Version(1, 0, 0, 0), startup.FromVersion);
        Assert.Equal(new Version(1, 1, 0, 0), startup.ToVersion);
        Assert.Equal(new Version(1, 1, 0, 0), startup.CurrentVersion);
        Assert.True(startup.IsUpdateEnabled);
        Assert.Equal(InstallationFixture.AppId, startup.AppId);
        Assert.Equal([(1234, StartupRunner.WaitForOldProcessTimeout)], world.Waiter.Calls);
        Assert.Empty(world.Fixture.Leftovers());
        Assert.Equal(JournalStatus.Missing, world.Fixture.ReadJournal().Status);
        Assert.Empty(world.Fixture.CacheFiles());
        Assert.True(File.Exists(world.Fixture.Layout.ReportsFile));
        Assert.Contains("\"eventType\":\"Updated\"", File.ReadAllText(world.Fixture.Layout.ReportsFile), StringComparison.Ordinal);

        // ---- 第二进程的客户端：补报 Updated，feed 走 ETag
        using (UpdateClient client = world.Client())
        {
            using var cts = new CancellationTokenSource();
            client.Failed += (_, e) => Assert.Fail($"不应失败：{e.Stage} {e.Exception}");
            Task run = client.RunAsync(cts.Token);
            await world.WaitForReportsAsync(2);                                              // Updated + Heartbeat
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        UpdateReport updated = Assert.Single(world.Reports, r => r.EventType == UpdateEventType.Updated);
        Assert.Equal(new Version(1, 0, 0, 0), updated.FromVersion);
        Assert.Equal(new Version(1, 1, 0, 0), updated.ToVersion);
        Assert.Equal(InstallationFixture.DeviceGuid, updated.DeviceGuid);
        Assert.Equal("PC-E2E", updated.MachineName);
        Assert.Contains(world.Reports, r => r.EventType == UpdateEventType.Heartbeat && r.ToVersion == new Version(1, 1, 0, 0));

        List<RecordedRequest> feedRequests = world.Server.SnapshotRequests().Where(r => r.Uri.AbsolutePath == "/updates/releases.json").ToList();
        Assert.Equal(3, feedRequests.Count);
        Assert.Null(feedRequests[0].IfNoneMatch);
        Assert.Equal("\"f1\"", feedRequests[1].IfNoneMatch);                                 // 第二进程带 ETag → 304
        Assert.Null(feedRequests[2].IfNoneMatch);                                            // 无缓存文档 → 无条件重取

        string log = world.LogText();
        Assert.Contains("检查", log, StringComparison.Ordinal);
        Assert.Contains("下载", log, StringComparison.Ordinal);
        Assert.Contains("哈希", log, StringComparison.Ordinal);
        Assert.Contains("journal", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("已启动新进程", log, StringComparison.Ordinal);
        Assert.Contains("done", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Corrupt_package_hash_is_reported_with_the_log_tail_and_leaves_the_installation_untouched()
    {
        using var world = new World(corruptSha: true);
        // state.json 与日志本轮必然改变（FeedETag / LastCheckedAt、新建 logs/*），这里断言的是应用文件零改动。
        IReadOnlyList<string> before = AppFiles(world.Fixture.Snapshot());
        var failed = new List<UpdateFailedEventArgs>();

        using (UpdateClient client = world.Client())
        {
            using var cts = new CancellationTokenSource();
            client.Failed += (_, e) => failed.Add(e);
            Task run = client.RunAsync(cts.Token);
            await world.WaitForReportsAsync(2);                                              // Failed + Heartbeat
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        UpdateFailedEventArgs e = Assert.Single(failed);
        Assert.Equal(UpdateStage.Verify, e.Stage);
        Assert.Equal(new Version(1, 1, 0, 0), e.Version);
        UpdateReport report = Assert.Single(world.Reports, r => r.EventType == UpdateEventType.Failed);
        Assert.Equal(UpdateStage.Verify, report.Stage);
        Assert.Equal(new Version(1, 0, 0, 0), report.FromVersion);
        Assert.Equal(new Version(1, 1, 0, 0), report.ToVersion);
        Assert.NotNull(report.LogTail);
        Assert.Contains("哈希", report.LogTail, StringComparison.Ordinal);
        Assert.Equal(before, AppFiles(world.Fixture.Snapshot()));                              // 应用文件零改动
        Assert.Equal(JournalStatus.Missing, world.Fixture.ReadJournal().Status);
        Assert.Empty(world.Fixture.CacheFiles());                                             // 校验失败的包已删
        Assert.Empty(world.Launcher.Launches);
        world.Fixture.AssertStandardV1();
    }

    private static string[] AppFiles(IReadOnlyList<string> snapshot)
        => [.. snapshot.Where(s => !s.StartsWith(".smartupdater/", StringComparison.Ordinal))];
}
