namespace SimpleOneX.SmartUpdater.Samples.WinForms.Tests;

public sealed class E2eHookOptionsTests
{
    [Fact]
    public void No_arguments_means_every_hook_is_off()
    {
        E2eHookOptions o = E2eHookOptions.Parse([]);

        // 关键契约：不传任何选项时，示例的行为与真实用户完全一致。
        Assert.Null(o.FeedUrl);
        Assert.Null(o.ReportUrl);
        Assert.Null(o.EventLogPath);
        Assert.Null(o.AnswerModal);
        Assert.Null(o.ShotDirectory);
        Assert.False(o.HangShutdown);
        Assert.Null(o.LocalAppDataDirectory);
        Assert.Null(o.PublicKey);
        Assert.Null(o.PollInterval);
        Assert.Null(o.JitterWindow);
        Assert.Null(o.ShutdownTimeout);
        Assert.False(o.AllowVersionDowngrade);
        Assert.Null(o.ExitAfter);
        Assert.Empty(o.UnknownOptions);
        Assert.Empty(o.BadOptions);
    }

    [Fact]
    public void The_packages_own_switches_are_not_mistaken_for_unknown_options()
    {
        // SmartUpdaterApp.Run 会收到这三个；E2eHooks 必须认得它们而不是报 unknown-option。
        E2eHookOptions o = E2eHookOptions.Parse(
            ["--smartupdater-updated", "1.1.0", "--smartupdater-wait-pid", "1234", "--smartupdater-rollback"]);

        Assert.Empty(o.UnknownOptions);
        Assert.Empty(o.BadOptions);
    }

    [Fact]
    public void Every_option_round_trips()
    {
        E2eHookOptions o = E2eHookOptions.Parse([
            "--feed-url", "http://127.0.0.1:5000/releases.json",
            "--report-url", "http://127.0.0.1:5000/api/v1/update-reports",
            "--local-app-data", @"C:\tmp\lad",
            "--public-key", "BASE64==",
            "--poll-seconds", "2",
            "--jitter-seconds", "0",
            "--shutdown-seconds", "15",
            "--allow-downgrade",
            "--event-log", @"C:\tmp\events.log",
            "--answer-modal", "skip",
            "--shot-dir", @"C:\tmp\shots",
            "--hang-shutdown",
            "--exit-after-seconds", "300",
        ]);

        Assert.Equal("http://127.0.0.1:5000/releases.json", o.FeedUrl);
        Assert.Equal("http://127.0.0.1:5000/api/v1/update-reports", o.ReportUrl);
        Assert.Equal(@"C:\tmp\lad", o.LocalAppDataDirectory);
        Assert.Equal("BASE64==", o.PublicKey);
        Assert.Equal(TimeSpan.FromSeconds(2), o.PollInterval);
        Assert.Equal(TimeSpan.Zero, o.JitterWindow);
        Assert.Equal(TimeSpan.FromSeconds(15), o.ShutdownTimeout);
        Assert.True(o.AllowVersionDowngrade);
        Assert.Equal(@"C:\tmp\events.log", o.EventLogPath);
        Assert.Equal(UpdateChoice.Skip, o.AnswerModal);
        Assert.Equal(@"C:\tmp\shots", o.ShotDirectory);
        Assert.True(o.HangShutdown);
        Assert.Equal(TimeSpan.FromSeconds(300), o.ExitAfter);
        Assert.Empty(o.UnknownOptions);
        Assert.Empty(o.BadOptions);
    }

    // [Theory] 的参数不能是 internal 枚举（CS0051），所以用 string + Enum.Parse。
    [Theory]
    [InlineData("now", "UpdateNow")]
    [InlineData("later", "Later")]
    [InlineData("skip", "Skip")]
    public void Answer_modal_accepts_the_three_documented_values(string value, string expectedName)
    {
        UpdateChoice expected = Enum.Parse<UpdateChoice>(expectedName);

        Assert.Equal(expected, E2eHookOptions.Parse(["--answer-modal", value]).AnswerModal);
    }

    [Theory]
    [InlineData("NOW")]      // 大小写敏感：脚本必须逐字写对，免得静默走成别的分支
    [InlineData("accept")]
    [InlineData("")]
    public void An_invalid_answer_modal_value_disables_the_hook_instead_of_enabling_the_wrong_one(string value)
    {
        E2eHookOptions o = E2eHookOptions.Parse(["--answer-modal", value]);

        Assert.Null(o.AnswerModal);
        Assert.Contains("--answer-modal", o.BadOptions);
    }

    [Fact]
    public void An_option_without_a_value_is_reported_and_does_not_swallow_the_next_option()
    {
        E2eHookOptions o = E2eHookOptions.Parse(["--event-log", "--hang-shutdown"]);

        Assert.Null(o.EventLogPath);
        Assert.Contains("--event-log", o.BadOptions);
        Assert.True(o.HangShutdown);       // 后面的开关不能被吞掉
    }

    [Fact]
    public void A_value_option_at_the_very_end_is_reported_as_bad()
    {
        E2eHookOptions o = E2eHookOptions.Parse(["--shot-dir"]);

        Assert.Null(o.ShotDirectory);
        Assert.Contains("--shot-dir", o.BadOptions);
    }

    [Fact]
    public void Unknown_options_are_collected_rather_than_fatal()
    {
        E2eHookOptions o = E2eHookOptions.Parse(["--not-a-thing", "--feed-url", "http://x/f.json"]);

        Assert.Contains("--not-a-thing", o.UnknownOptions);
        Assert.Equal("http://x/f.json", o.FeedUrl);
    }

    [Fact]
    public void Shot_file_names_get_a_numeric_suffix_when_taken_again()
    {
        // 端到端脚本要连拍三次：同名已存在就依次加 -2、-3。
        HashSet<string> taken = [];
        string first = E2eHooks.NextFreePath("d", "01-before.png", taken.Contains);
        taken.Add(first);
        string second = E2eHooks.NextFreePath("d", "01-before.png", taken.Contains);
        taken.Add(second);
        string third = E2eHooks.NextFreePath("d", "01-before.png", taken.Contains);

        Assert.Equal(Path.Combine("d", "01-before.png"), first);
        Assert.Equal(Path.Combine("d", "01-before-2.png"), second);
        Assert.Equal(Path.Combine("d", "01-before-3.png"), third);
    }

    // ---- 重启后钩子选项的接力 ----
    // 更新完成后，包用固定的 --smartupdater-updated/--smartupdater-wait-pid 重启示例，
    // 钩子选项会全丢：新进程不写 started、不截 03-after.png，端到端脚本只能超时。
    // 对策放在示例侧：把钩子选项导出到环境变量，新进程继承后补回来。

    [Fact]
    public void Hook_arguments_survive_a_round_trip_through_the_environment_variable()
    {
        string[] full = [
            "--feed-url", "http://127.0.0.1:5000/releases.json",
            "--report-url", "http://127.0.0.1:5000/api/v1/update-reports",
            "--local-app-data", @"C:\tmp\lad",
            "--public-key", "BASE64==",
            "--poll-seconds", "2",
            "--jitter-seconds", "0",
            "--shutdown-seconds", "15",
            "--allow-downgrade",
            "--event-log", @"C:\tmp\events.log",
            "--answer-modal", "skip",
            "--shot-dir", @"C:\tmp\shots",
            "--hang-shutdown",
            "--exit-after-seconds", "300",
        ];

        // 一个都不能漏：SelectHookArguments 的两张表与 Parse 的 switch 一致，就靠这条钉住。
        Assert.Equal(full, E2eHookOptions.SelectHookArguments(full));
        Assert.Equal(full, E2eHookOptions.ParseCarryOver(E2eHookOptions.FormatCarryOver(full)));
    }

    [Fact]
    public void The_packages_own_restart_switches_are_not_carried_over()
    {
        string[] args = [
            "--smartupdater-updated", "1.1.0.0", "--smartupdater-wait-pid", "4242", "--smartupdater-rollback",
            "--feed-url", "http://x/f.json", "--not-a-thing", "stray",
        ];

        // 带过去的只有钩子选项：包的开关由下一次重启自己给，不认识的 token 不该被无限接力。
        Assert.Equal(["--feed-url", "http://x/f.json"], E2eHookOptions.SelectHookArguments(args));
    }

    [Fact]
    public void A_real_user_never_writes_the_carry_over_variable()
    {
        Assert.Null(E2eHookOptions.FormatCarryOver([]));
        Assert.Null(E2eHookOptions.FormatCarryOver(["--smartupdater-updated", "1.1.0.0", "--smartupdater-wait-pid", "7"]));
    }

    [Fact]
    public void The_restarted_process_picks_the_hook_options_up_from_the_environment()
    {
        string[] restart = ["--smartupdater-updated", "1.1.0.0", "--smartupdater-wait-pid", "4242"];
        string carried = E2eHookOptions.FormatCarryOver([
            "--feed-url", "http://127.0.0.1:5000/releases.json",
            "--event-log", @"C:\tmp\e2e events.log",     // 带空格的路径必须原样活下来
            "--shot-dir", @"C:\tmp\shots",
            "--answer-modal", "now",
        ])!;

        string[] resolved = E2eHookOptions.ResolveArguments(restart, name => name == E2eHookOptions.CarryOverVariable ? carried : null);
        E2eHookOptions o = E2eHookOptions.Parse(resolved);

        Assert.Equal(restart, resolved.Take(restart.Length));       // 包自己的参数排在前面，没被动过
        Assert.Equal("http://127.0.0.1:5000/releases.json", o.FeedUrl);
        Assert.Equal(@"C:\tmp\e2e events.log", o.EventLogPath);
        Assert.Equal(@"C:\tmp\shots", o.ShotDirectory);
        Assert.Equal(UpdateChoice.UpdateNow, o.AnswerModal);
        Assert.Empty(o.UnknownOptions);
        Assert.Empty(o.BadOptions);
    }

    [Fact]
    public void The_command_line_wins_over_the_environment()
    {
        string[] args = ["--feed-url", "http://from-command-line/f.json"];
        string carried = E2eHookOptions.FormatCarryOver(["--feed-url", "http://from-environment/f.json"])!;

        string[] resolved = E2eHookOptions.ResolveArguments(args, _ => carried);

        Assert.Equal(args, resolved);
    }

    [Fact]
    public void Without_the_variable_the_command_line_is_untouched()
    {
        string[] args = ["--smartupdater-updated", "1.1.0.0"];

        Assert.Equal(args, E2eHookOptions.ResolveArguments(args, _ => null));
        Assert.Equal(args, E2eHookOptions.ResolveArguments(args, _ => string.Empty));
        Assert.Empty(E2eHookOptions.ResolveArguments([], _ => null));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void A_non_numeric_or_negative_duration_is_rejected(string value)
    {
        E2eHookOptions o = E2eHookOptions.Parse(["--poll-seconds", value]);

        Assert.Null(o.PollInterval);
        Assert.Contains("--poll-seconds", o.BadOptions);
    }
}
