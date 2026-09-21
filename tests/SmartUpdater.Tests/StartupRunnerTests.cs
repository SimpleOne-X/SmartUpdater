using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class StartupRunnerTests
{
    private const string Exe = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe";

    private sealed class Harness
    {
        public FakeUpdateEngine Engine { get; } = new();
        public FakeProcessWaiter Waiter { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public List<string> Calls { get; } = [];

        public UpdateEnvironment Env(string[]? commandLine = null) => new()
        {
            ProcessPath = Exe,
            CommandLineArguments = commandLine ?? [Exe],
            EntryAssemblyName = "MyApp",
            EntryAssemblyVersion = new Version(1, 2, 3, 0),
            InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
            LocalApplicationDataDirectory = @"C:\Users\x\AppData\Local",
            ProcessWaiter = new OrderRecordingWaiter(this),
            TimeProvider = Time,
            MachineName = "PC-01",
        };

        public StartupRunner Runner(string? appId = null, string[]? commandLine = null)
            => new(Env(commandLine), appId, _ => Engine);

        private sealed class OrderRecordingWaiter(Harness h) : IProcessWaiter
        {
            public bool WaitForExit(int processId, TimeSpan timeout)
            {
                h.Calls.Add($"wait:{processId}:{(int)timeout.TotalSeconds}:recovery={h.Engine.RecoveryCalls}");
                return h.Waiter.WaitForExit(processId, timeout);
            }
        }
    }

    [Fact]
    public void Plain_start_without_journal_reports_nothing_and_enables_updates()
    {
        var h = new Harness();

        StartupResult result = h.Runner().Run([]);

        Assert.False(result.JustUpdated);
        Assert.Null(result.FromVersion);
        Assert.Null(result.ToVersion);
        Assert.Equal(new Version(1, 2, 3, 0), result.CurrentVersion);
        Assert.True(result.IsUpdateEnabled);
        Assert.Null(result.UpdateDisabledReason);
        Assert.False(result.IsRollbackApplied);
        Assert.Equal(AppIdentity.Derive("MyApp", Exe), result.AppId);
        Assert.Equal(1, h.Engine.RecoveryCalls);
        Assert.Empty(h.Engine.Queued);
        Assert.True(h.Engine.IsDisposed);
    }

    [Fact]
    public void Completed_update_yields_JustUpdated_with_canonical_versions_and_queues_an_Updated_report()
    {
        var h = new Harness();
        h.Engine.Recovery = new RecoveryResult(RecoveryAction.CompletedUpdate, new Version(1, 2, 3), new Version(1, 2, 4), []);
        h.Engine.State.CurrentVersion = new Version(1, 2, 4);

        StartupResult result = h.Runner().Run([SmartUpdaterArgs.Updated, "1.2.4.0", SmartUpdaterArgs.WaitPid, "999"]);

        Assert.True(result.JustUpdated);
        Assert.Equal(new Version(1, 2, 3, 0), result.FromVersion);
        Assert.Equal(new Version(1, 2, 4, 0), result.ToVersion);
        Assert.Equal(new Version(1, 2, 4, 0), result.CurrentVersion);
        UpdateReport report = Assert.Single(h.Engine.Queued);
        Assert.Equal(UpdateEventType.Updated, report.EventType);
        Assert.Equal(new Version(1, 2, 3, 0), report.FromVersion);
        Assert.Equal(new Version(1, 2, 4, 0), report.ToVersion);
        Assert.Equal(FakeUpdateEngine.DeviceGuid, report.DeviceGuid);
        Assert.Equal("PC-01", report.MachineName);
        Assert.True(report.IsSuccess);
    }

    [Fact]
    public void Old_process_is_awaited_before_recovery_with_a_30_second_cap()
    {
        var h = new Harness();

        h.Runner().Run([SmartUpdaterArgs.WaitPid, "4321"]);

        Assert.Equal(["wait:4321:30:recovery=0"], h.Calls);
        Assert.Equal(1, h.Engine.RecoveryCalls);
    }

    [Fact]
    public void Old_process_not_exiting_in_time_is_logged_and_startup_continues()
    {
        var h = new Harness();
        h.Waiter.Result = false;

        StartupResult result = h.Runner().Run([SmartUpdaterArgs.WaitPid, "4321"]);

        Assert.True(result.IsUpdateEnabled);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("4321", StringComparison.Ordinal));
    }

    public static TheoryData<string[]> BadWaitPidArguments => new()
    {
        new[] { "--smartupdater-wait-pid" },
        new[] { "--smartupdater-wait-pid", "--smartupdater-updated", "1.0" },
        new[] { "--smartupdater-wait-pid", "abc" },
        new[] { "--smartupdater-wait-pid", "-5" },
    };

    public static TheoryData<string[]> BadUpdatedArguments => new()
    {
        new[] { "--smartupdater-updated" },
        new[] { "--smartupdater-updated", "--smartupdater-wait-pid", "12" },
        new[] { "--smartupdater-updated", "not-a-version" },
    };

    [Theory]
    [MemberData(nameof(BadWaitPidArguments))]
    public void Missing_or_invalid_wait_pid_value_is_logged_and_ignored(string[] args)
    {
        var h = new Harness();

        StartupResult result = h.Runner().Run(args);

        Assert.Empty(h.Calls);
        Assert.True(result.IsUpdateEnabled);
        Assert.NotEmpty(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning));
    }

    [Theory]
    [MemberData(nameof(BadUpdatedArguments))]
    public void Updated_flag_without_a_usable_value_is_logged_and_does_not_fake_an_update(string[] args)
    {
        var h = new Harness();

        StartupResult result = h.Runner().Run(args);

        Assert.False(result.JustUpdated);
        Assert.True(result.IsUpdateEnabled);
        Assert.Empty(h.Engine.Queued);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains(SmartUpdaterArgs.Updated, StringComparison.Ordinal));
    }

    [Fact]
    public void Updated_flag_without_a_done_journal_is_informational_only()
    {
        var h = new Harness();

        StartupResult result = h.Runner().Run([SmartUpdaterArgs.Updated, "1.2.4"]);

        Assert.False(result.JustUpdated);
        Assert.Empty(h.Engine.Queued);
        Assert.True(h.Engine.RecordingLog.Contains(SmartUpdaterArgs.Updated));
    }

    [Fact]
    public void Updated_flag_disagreeing_with_the_journal_is_logged_but_the_journal_wins()
    {
        var h = new Harness();
        h.Engine.Recovery = new RecoveryResult(RecoveryAction.CompletedUpdate, new Version(1, 0), new Version(1, 1), []);

        StartupResult result = h.Runner().Run([SmartUpdaterArgs.Updated, "9.9.9"]);

        Assert.True(result.JustUpdated);
        Assert.Equal(new Version(1, 1, 0, 0), result.ToVersion);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("9.9.9", StringComparison.Ordinal));
    }

    [Fact]
    public void Rollback_flag_restores_backups_deletes_the_journal_and_skips_recovery()
    {
        var h = new Harness();
        h.Engine.InstalledVersion = new Version(1, 2, 2);
        h.Engine.State.CurrentVersion = new Version(1, 2, 3);

        StartupResult result = h.Runner().Run([SmartUpdaterArgs.Rollback]);

        Assert.True(result.IsRollbackApplied);
        Assert.Equal(1, h.Engine.RollbackFromBackupsCalls);
        Assert.Equal(1, h.Engine.DeleteJournalCalls);
        Assert.Equal(0, h.Engine.RecoveryCalls);
        Assert.Equal(new Version(1, 2, 2), h.Engine.State.CurrentVersion);
        Assert.Equal(new Version(1, 2, 2, 0), result.CurrentVersion);
        Assert.False(result.JustUpdated);
        Assert.Empty(h.Engine.Queued);
    }

    [Fact]
    public void Rollback_errors_are_reported_as_Failed_Rollback()
    {
        var h = new Harness();
        h.Engine.RollbackResult = new RecoveryResult(RecoveryAction.RolledBack, null, null, ["IOException: app.exe.suold 被占用"]);

        h.Runner().Run([SmartUpdaterArgs.Rollback]);

        UpdateReport report = Assert.Single(h.Engine.Queued);
        Assert.Equal(UpdateEventType.Failed, report.EventType);
        Assert.Equal(UpdateStage.Rollback, report.Stage);
        Assert.Contains("app.exe.suold", report.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_errors_are_reported_as_Failed_Rollback_even_when_the_update_completed()
    {
        var h = new Harness();
        h.Engine.Recovery = new RecoveryResult(RecoveryAction.CompletedUpdate, new Version(1, 0), new Version(1, 1), ["UnauthorizedAccessException: old.dll.suold"]);

        StartupResult result = h.Runner().Run([]);

        Assert.True(result.JustUpdated);
        Assert.Equal(2, h.Engine.Queued.Count);
        Assert.Contains(h.Engine.Queued, r => r.EventType == UpdateEventType.Updated);
        Assert.Contains(h.Engine.Queued, r => r.EventType == UpdateEventType.Failed && r.Stage == UpdateStage.Rollback && r.ErrorMessage!.Contains("old.dll.suold", StringComparison.Ordinal));
    }

    [Fact]
    public void Rolled_back_startup_is_not_JustUpdated_and_reports_the_rollback()
    {
        var h = new Harness();
        h.Engine.Recovery = new RecoveryResult(RecoveryAction.RolledBack, new Version(1, 0), new Version(1, 1), ["IOException: x"]);

        StartupResult result = h.Runner().Run([]);

        Assert.False(result.JustUpdated);
        Assert.Equal(UpdateStage.Rollback, Assert.Single(h.Engine.Queued).Stage);
    }

    [Fact]
    public void Unwritable_install_directory_disables_updates_and_reports_PermissionCheck()
    {
        var h = new Harness();
        h.Engine.ProbeResult = new InstallDirectoryProbeResult(false, "UnauthorizedAccessException: 拒绝访问");

        StartupResult result = h.Runner().Run([]);

        Assert.False(result.IsUpdateEnabled);
        Assert.Equal("UnauthorizedAccessException: 拒绝访问", result.UpdateDisabledReason);
        UpdateReport report = Assert.Single(h.Engine.Queued);
        Assert.Equal(UpdateEventType.Failed, report.EventType);
        Assert.Equal(UpdateStage.PermissionCheck, report.Stage);
        Assert.NotEmpty(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error));
    }

    [Fact]
    public void Null_args_fall_back_to_the_process_command_line_without_argv0()
    {
        var h = new Harness();

        h.Runner(commandLine: [Exe, SmartUpdaterArgs.WaitPid, "77"]).Run(null);

        Assert.Equal(["wait:77:30:recovery=0"], h.Calls);
    }

    [Fact]
    public void Current_version_falls_back_from_state_to_manifest_to_assembly_and_persists()
    {
        var h = new Harness();
        h.Engine.State.CurrentVersion = null;
        h.Engine.InstalledVersion = new Version(2, 0);

        StartupResult fromManifest = h.Runner().Run([]);
        Assert.Equal(new Version(2, 0, 0, 0), fromManifest.CurrentVersion);
        Assert.Equal(new Version(2, 0, 0, 0), h.Engine.State.CurrentVersion);

        var h2 = new Harness();
        h2.Engine.State.CurrentVersion = null;
        h2.Engine.InstalledVersion = null;
        Assert.Equal(new Version(1, 2, 3, 0), h2.Runner().Run([]).CurrentVersion);

        var h3 = new Harness();
        h3.Engine.State.CurrentVersion = null;
        var env = h3.Env();
        var runner = new StartupRunner(new UpdateEnvironment
        {
            ProcessPath = env.ProcessPath,
            CommandLineArguments = env.CommandLineArguments,
            EntryAssemblyName = env.EntryAssemblyName,
            EntryAssemblyVersion = null,
            InstallDirectory = env.InstallDirectory,
            LocalApplicationDataDirectory = env.LocalApplicationDataDirectory,
            TimeProvider = h3.Time,
        }, null, _ => h3.Engine);
        Assert.Equal(VersionNormalization.Zero, runner.Run([]).CurrentVersion);
        Assert.NotEmpty(h3.Engine.RecordingLog.AtLevel(UpdateLogLevel.Warning));
    }

    [Fact]
    public void Explicit_app_id_is_used_and_invalid_one_throws()
    {
        var h = new Harness();

        Assert.Equal("Contoso.Payroll", h.Runner("Contoso.Payroll").Run([]).AppId);
        Assert.Throws<ArgumentException>(() => h.Runner("..").Run([]));
    }

    [Fact]
    public void Unexpected_engine_failure_degrades_to_disabled_updates_instead_of_throwing()
    {
        var h = new Harness();
        h.Engine.RecoveryException = new InvalidOperationException("journal store exploded");

        StartupResult result = h.Runner().Run([]);

        Assert.False(result.IsUpdateEnabled);
        Assert.Contains("journal store exploded", result.UpdateDisabledReason, StringComparison.Ordinal);
        Assert.Contains(h.Engine.RecordingLog.AtLevel(UpdateLogLevel.Error), e => e.Exception is InvalidOperationException);
        Assert.True(h.Engine.IsDisposed);
    }

    [Fact]
    public void Missing_process_path_falls_back_to_the_install_directory_for_the_app_id()
    {
        var h = new Harness();
        UpdateEnvironment env = h.Env();
        var runner = new StartupRunner(new UpdateEnvironment
        {
            ProcessPath = null,
            CommandLineArguments = [],
            EntryAssemblyName = "MyApp",
            InstallDirectory = env.InstallDirectory,
            LocalApplicationDataDirectory = env.LocalApplicationDataDirectory,
            TimeProvider = h.Time,
        }, null, _ => h.Engine);

        StartupResult result = runner.Run([]);

        Assert.Equal(AppIdentity.Derive("MyApp", env.InstallDirectory), result.AppId);
        Assert.True(result.IsUpdateEnabled);
    }
}
