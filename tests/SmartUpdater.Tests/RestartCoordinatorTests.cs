using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class RestartCoordinatorTests
{
    private const string Exe = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe";

    private sealed class Harness
    {
        public FakeUpdateEngine Engine { get; } = new();
        public FakeProcessLauncher Launcher { get; } = new();
        public RecordingEventSink Events { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public string? ExecutablePath { get; set; }
        public string ProcessPath { get; set; } = Exe;
        public string[] CommandLine { get; set; } = [Exe];

        public UpdateEnvironment Env => new()
        {
            ProcessPath = ProcessPath,
            CommandLineArguments = CommandLine,
            EntryAssemblyName = "MyApp",
            InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
            ProcessId = 1234,
            TimeProvider = Time,
            ProcessLauncher = Launcher,
        };

        public RestartCoordinator Coordinator()
            => new(ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ShutdownTimeout = ShutdownTimeout, ExecutablePath = ExecutablePath }, Env), Engine, Events, Env);
    }

    [Fact]
    public async Task Raises_Restarting_then_launches_the_new_process_with_handover_arguments()
    {
        var h = new Harness();

        bool launched = await h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);

        Assert.True(launched);
        RestartingEventArgs args = Assert.Single(h.Events.Restarting);
        Assert.Equal(new Version(1, 2, 4, 0), args.Version);
        Launch launch = Assert.Single(h.Launcher.Launches);
        Assert.Equal(Exe, launch.FileName);
        Assert.Equal("--smartupdater-updated 1.2.4.0 --smartupdater-wait-pid 1234", launch.Arguments);
        Assert.Equal(h.Engine.Layout.InstallDirectory, launch.WorkingDirectory);
        Assert.True(h.Engine.RecordingLog.Contains("1.2.4.0"));
    }

    [Fact]
    public async Task Waits_for_completed_handler_tasks_without_touching_the_clock()
    {
        var h = new Harness();
        var done = new TaskCompletionSource();
        h.Events.RestartingBehavior = e => { e.WaitFor(done.Task); e.WaitFor(Task.CompletedTask); };
        done.SetResult();

        bool launched = await h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);

        Assert.True(launched);
        Assert.Equal(0, h.Time.ActiveTimerCount);
        Assert.Empty(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning));
    }

    [Fact]
    public async Task Never_completing_handler_task_is_cut_off_at_ShutdownTimeout()
    {
        var h = new Harness();
        var never = new TaskCompletionSource();
        h.Events.RestartingBehavior = e => e.WaitFor(never.Task);

        Task<bool> restart = h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);
        await h.Time.WaitForTimersAsync(1);
        Assert.Empty(h.Launcher.Launches);
        h.Time.Advance(TimeSpan.FromSeconds(14));
        Assert.Empty(h.Launcher.Launches);
        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await restart.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Single(h.Launcher.Launches);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("15", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Faulted_handler_task_is_logged_and_restart_proceeds()
    {
        var h = new Harness();
        h.Events.RestartingBehavior = e => e.WaitFor(Task.FromException(new InvalidOperationException("save failed")));

        bool launched = await h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);

        Assert.True(launched);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Zero_shutdown_timeout_does_not_wait()
    {
        var h = new Harness { ShutdownTimeout = TimeSpan.Zero };
        h.Events.RestartingBehavior = e => e.WaitFor(new TaskCompletionSource().Task);

        bool launched = await h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);

        Assert.True(launched);
        Assert.Equal(0, h.Time.ActiveTimerCount);
    }

    [Fact]
    public async Task Launch_failure_returns_false_and_logs_an_error()
    {
        var h = new Harness();
        h.Launcher.ThrowOnStart = new System.ComponentModel.Win32Exception(2, "找不到文件");

        bool launched = await h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), CancellationToken.None);

        Assert.False(launched);
        Assert.Single(h.Launcher.Launches);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error), e => e.Exception is System.ComponentModel.Win32Exception);
    }

    [Fact]
    public async Task Cancellation_after_the_point_of_no_return_is_ignored()
    {
        var h = new Harness();
        var never = new TaskCompletionSource();
        h.Events.RestartingBehavior = e => e.WaitFor(never.Task);
        using var cts = new CancellationTokenSource();

        Task<bool> restart = h.Coordinator().RestartAsync(new Version(1, 2, 4, 0), cts.Token);
        await h.Time.WaitForTimersAsync(1);
        cts.Cancel();
        Assert.False(restart.IsCompleted);
        h.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.True(await restart.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dotnet_host_restart_goes_through_dotnet_with_the_dll_first()
    {
        var h = new Harness { ProcessPath = @"C:\Program Files\dotnet\dotnet.exe", CommandLine = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"] };

        await h.Coordinator().RestartAsync(new Version(2, 0, 0, 0), CancellationToken.None);

        Launch launch = Assert.Single(h.Launcher.Launches);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", launch.FileName);
        Assert.Equal(CommandLine.Quote(@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll") + " --smartupdater-updated 2.0.0.0 --smartupdater-wait-pid 1234", launch.Arguments);
    }

    [Fact]
    public void BuildArguments_quotes_paths_with_spaces_and_omits_empty_prefix()
    {
        var plain = new ProcessIdentity(Exe, Exe, "", false);
        var dotnet = new ProcessIdentity(@"C:\My Apps\a.dll", "dotnet.exe", CommandLine.Quote(@"C:\My Apps\a.dll"), true);

        Assert.Equal("--smartupdater-updated 1.0.0.0 --smartupdater-wait-pid 7", RestartCoordinator.BuildArguments(plain, new Version(1, 0, 0, 0), 7));
        Assert.Equal("\"C:\\My Apps\\a.dll\" --smartupdater-updated 1.0.0.0 --smartupdater-wait-pid 7", RestartCoordinator.BuildArguments(dotnet, new Version(1, 0, 0, 0), 7));
        Assert.Equal(["--smartupdater-updated", "1.0.0.0", "--smartupdater-wait-pid", "7"], CommandLineTestHelper.SplitLikeWindows(RestartCoordinator.BuildArguments(plain, new Version(1, 0, 0, 0), 7)));
    }
}
