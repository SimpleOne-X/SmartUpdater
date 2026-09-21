using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater.Packer.Tests.Gui;

/// <summary>GUI 里与界面无关的纯逻辑：表单校验、参数组装、命令执行与结果格式化。源文件在 Gui 项目的 Logic 目录，这里以链接方式编译。</summary>
public sealed class PackerGuiLogicTests
{
    private static PackForm ValidPack() => new()
    {
        InputDirectory = @"C:\publish",
        OutputDirectory = @"C:\release",
        Version = "1.2.4.0",
    };

    // ---- 校验：pack ----

    [Fact]
    public void Valid_minimal_pack_form_has_no_errors()
        => Assert.Empty(FormValidator.Validate(ValidPack()));

    [Theory]
    [InlineData("", "应用目录")]
    [InlineData("  ", "应用目录")]
    public void Pack_requires_input_directory(string input, string expectedFragment)
    {
        IReadOnlyList<string> errors = FormValidator.Validate(ValidPack() with { InputDirectory = input });
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Pack_requires_output_directory()
    {
        IReadOnlyList<string> errors = FormValidator.Validate(ValidPack() with { OutputDirectory = "" });
        Assert.Contains(errors, e => e.Contains("输出目录", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.4")]
    [InlineData("1.2")]
    [InlineData("1.2.4.5.6")]
    [InlineData("a.b.c.d")]
    [InlineData("1.2.4.-1")]
    [InlineData("1.2.4.")]
    public void Pack_version_must_have_exactly_four_numeric_segments(string version)
    {
        IReadOnlyList<string> errors = FormValidator.Validate(ValidPack() with { Version = version });
        Assert.Contains(errors, e => e.Contains("版本号", StringComparison.Ordinal));
    }

    [Fact]
    public void Pack_min_updatable_from_when_given_must_be_a_version()
    {
        Assert.Empty(FormValidator.Validate(ValidPack() with { MinUpdatableFrom = "1.0.0" }));
        IReadOnlyList<string> errors = FormValidator.Validate(ValidPack() with { MinUpdatableFrom = "x" });
        Assert.Contains(errors, e => e.Contains("最低可升级版本", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("100", true)]
    [InlineData("50", true)]
    [InlineData("", true)]
    [InlineData("101", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("5.5", false)]
    public void Pack_rollout_percent_is_an_integer_from_0_to_100_or_empty(string percent, bool valid)
    {
        IReadOnlyList<string> errors = FormValidator.Validate(ValidPack() with { RolloutPercent = percent });
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Pack_collects_all_errors_at_once()
    {
        IReadOnlyList<string> errors = FormValidator.Validate(new PackForm());
        Assert.Equal(3, errors.Count);
    }

    // ---- 校验：sign ----

    [Fact]
    public void Sign_requires_feed_and_key()
    {
        IReadOnlyList<string> errors = FormValidator.Validate(new SignForm());
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("releases.json", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("私钥", StringComparison.Ordinal));
        Assert.Empty(FormValidator.Validate(new SignForm { FeedPath = "f.json", KeyPath = "k.pem" }));
    }

    // ---- 参数组装 ----

    [Fact]
    public void Minimal_pack_form_builds_only_required_options_with_equals_form()
    {
        string[] args = CommandLineBuilder.BuildPack(ValidPack());
        Assert.Equal(
            ["pack", @"--input=C:\publish", "--version=1.2.4.0", @"--output=C:\release", "--mode=optional"],
            args);
    }

    [Fact]
    public void Full_pack_form_builds_every_selected_option()
    {
        PackForm form = ValidPack() with
        {
            PackageName = "MyApp",
            Channel = "beta",
            IsMandatory = true,
            RolloutPercent = "30",
            MinUpdatableFrom = "1.0.0.0",
            Notes = "修复若干问题",
            Preserve = "config/**\r\n\r\n  *.user  \n",
            Force = true,
        };

        string[] args = CommandLineBuilder.BuildPack(form);

        Assert.Equal("pack", args[0]);
        Assert.Contains("--package-name=MyApp", args);
        Assert.Contains("--channel=beta", args);
        Assert.Contains("--mode=mandatory", args);
        Assert.Contains("--rollout-percent=30", args);
        Assert.Contains("--min-updatable-from=1.0.0.0", args);
        Assert.Contains("--notes=修复若干问题", args);
        Assert.Contains("--force", args);
        Assert.Equal(["--preserve=config/**", "--preserve=*.user"], args.Where(a => a.StartsWith("--preserve=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Blank_optional_fields_are_omitted_and_force_is_a_bare_switch_only_when_set()
    {
        string[] args = CommandLineBuilder.BuildPack(ValidPack() with { Channel = "  ", Notes = "", PackageName = "" });
        Assert.DoesNotContain(args, a => a.StartsWith("--channel", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--notes", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--package-name", StringComparison.Ordinal));
        Assert.DoesNotContain("--force", args);
    }

    [Fact]
    public void Values_starting_with_double_dash_survive_because_equals_form_is_used()
    {
        string[] args = CommandLineBuilder.BuildPack(ValidPack() with { Notes = "--例外" });
        Assert.True(ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? error), error);
        Assert.Equal(["--例外"], parsed!.GetValues("notes"));
    }

    [Fact]
    public void Pack_form_arguments_are_accepted_by_the_real_parser()
    {
        PackForm form = ValidPack() with { Force = true, Preserve = "a\nb", Notes = "n" };
        Assert.True(ArgumentParser.TryParse(CommandLineBuilder.BuildPack(form), out ParsedCommandLine? parsed, out string? error), error);
        Assert.Null(parsed!.FindUnknownOption(PackCommand.KnownOptions));
    }

    [Fact]
    public void Sign_form_builds_feed_key_and_optional_resign()
    {
        Assert.Equal(
            ["sign", "--feed=f.json", "--key=k.pem"],
            CommandLineBuilder.BuildSign(new SignForm { FeedPath = "f.json", KeyPath = "k.pem" }));
        Assert.Equal(
            ["sign", "--feed=f.json", "--key=k.pem", "--resign"],
            CommandLineBuilder.BuildSign(new SignForm { FeedPath = "f.json", KeyPath = "k.pem", Resign = true }));
    }

    [Fact]
    public void Default_feed_path_is_releases_json_under_the_output_directory()
        => Assert.Equal(Path.Combine("out", "releases.json"), CommandLineBuilder.DefaultFeedPath("out"));

    // ---- 执行与结果格式化 ----

    [Fact]
    public void Runner_reports_a_parse_failure_as_usage_error_without_throwing()
    {
        CommandResult result = CommandRunner.Run(["nonsense"]);
        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Contains("未知命令", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_pack_then_sign_on_a_real_directory_succeeds_end_to_end()
    {
        using FakeApp app = FakeApp.Standard();
        using var temp = new TempDirectory();
        string output = temp.Combine("release");

        CommandResult pack = CommandRunner.Run(CommandLineBuilder.BuildPack(new PackForm
        {
            InputDirectory = app.Root,
            OutputDirectory = output,
            Version = "1.2.4.0",
        }));
        Assert.Equal(ExitCode.Success, pack.ExitCode);
        Assert.Contains("已生成升级包", pack.Output, StringComparison.Ordinal);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyPath = temp.Combine("private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        CommandResult sign = CommandRunner.Run(CommandLineBuilder.BuildSign(new SignForm
        {
            FeedPath = CommandLineBuilder.DefaultFeedPath(output),
            KeyPath = keyPath,
        }));
        Assert.Equal(ExitCode.Success, sign.ExitCode);
        Assert.Contains("1 条已签名", sign.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_returns_input_error_for_a_missing_directory()
    {
        CommandResult result = CommandRunner.Run(CommandLineBuilder.BuildPack(ValidPack() with
        {
            InputDirectory = Path.Combine(Path.GetTempPath(), "supack-missing-" + Guid.NewGuid().ToString("N")),
            OutputDirectory = Path.Combine(Path.GetTempPath(), "supack-out-" + Guid.NewGuid().ToString("N")),
        }));
        Assert.Equal(ExitCode.Input, result.ExitCode);
        Assert.Contains("不存在", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExitCode.Success, "成功")]
    [InlineData(ExitCode.Usage, "用法错误")]
    [InlineData(ExitCode.Input, "输入有问题")]
    [InlineData(ExitCode.Conflict, "冲突")]
    [InlineData(ExitCode.Unexpected, "意外错误")]
    public void Formatter_headline_names_the_command_the_exit_code_and_its_meaning(int code, string meaning)
    {
        string text = ResultFormatter.Format("pack", new CommandResult(code, "", ""));
        Assert.Contains("[pack]", text, StringComparison.Ordinal);
        Assert.Contains(meaning, text, StringComparison.Ordinal);
        Assert.Contains($"退出码 {code}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_appends_output_and_error_and_trims_trailing_blank_lines()
    {
        string text = ResultFormatter.Format("sign", new CommandResult(ExitCode.Conflict, "标准输出行\n\n", "错误行\n"));
        Assert.Contains("标准输出行", text, StringComparison.Ordinal);
        Assert.Contains("错误行", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void Formatter_handles_unknown_exit_codes()
        => Assert.Contains("退出码 99", ResultFormatter.Format("pack", new CommandResult(99, "", "")), StringComparison.Ordinal);
}
