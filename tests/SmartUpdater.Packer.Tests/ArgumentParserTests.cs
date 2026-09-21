namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class ArgumentParserTests
{
    private static ParsedCommandLine Parse(params string[] args)
    {
        Assert.True(ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? error), error);
        return parsed!;
    }

    private static string ParseError(params string[] args)
    {
        Assert.False(ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void Command_is_the_first_token()
    {
        ParsedCommandLine parsed = Parse("pack", "--input", "publish");

        Assert.Equal("pack", parsed.Command);
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("sign")]
    [InlineData("help")]
    public void Known_commands_are_accepted(string command) => Assert.Equal(command, Parse(command).Command);

    [Fact]
    public void Missing_command_is_rejected() => Assert.Contains("命令", ParseError());

    [Fact]
    public void Unknown_command_is_rejected() => Assert.Contains("publish", ParseError("publish"));

    [Fact]
    public void Space_separated_value_is_captured()
    {
        ParsedCommandLine parsed = Parse("pack", "--version", "1.2.4");

        Assert.Equal(["1.2.4"], parsed.GetValues("version"));
    }

    [Fact]
    public void Equals_separated_value_is_captured()
    {
        ParsedCommandLine parsed = Parse("pack", "--version=1.2.4");

        Assert.Equal(["1.2.4"], parsed.GetValues("version"));
    }

    [Fact]
    public void Equals_splits_at_the_first_equals_only()
    {
        ParsedCommandLine parsed = Parse("pack", "--notes=a=b=c");

        Assert.Equal(["a=b=c"], parsed.GetValues("notes"));
    }

    [Fact]
    public void Empty_value_after_equals_is_still_a_value()
    {
        ParsedCommandLine parsed = Parse("pack", "--notes=");

        Assert.Equal([""], parsed.GetValues("notes"));
        Assert.False(parsed.HasFlag("notes"));
    }

    [Fact]
    public void Option_followed_by_another_option_is_a_flag()
    {
        ParsedCommandLine parsed = Parse("pack", "--force", "--input", "publish");

        Assert.True(parsed.HasFlag("force"));
        Assert.Equal(["publish"], parsed.GetValues("input"));
    }

    [Fact]
    public void Trailing_option_is_a_flag()
    {
        ParsedCommandLine parsed = Parse("pack", "--force");

        Assert.True(parsed.HasFlag("force"));
    }

    [Fact]
    public void Repeated_options_keep_their_order()
    {
        ParsedCommandLine parsed = Parse("pack", "--preserve", "a.json", "--preserve", "b/*.db");

        Assert.Equal(["a.json", "b/*.db"], parsed.GetValues("preserve"));
    }

    [Fact]
    public void Has_option_distinguishes_absent_from_flag()
    {
        ParsedCommandLine parsed = Parse("pack", "--force");

        Assert.True(parsed.HasOption("force"));
        Assert.False(parsed.HasOption("input"));
    }

    [Theory]
    [InlineData("--Input")]
    [InlineData("--min_updatable_from")]
    [InlineData("--1abc")]
    [InlineData("--")]
    public void Non_kebab_case_option_names_are_rejected(string option)
    {
        string error = ParseError("pack", option, "x");

        Assert.Contains(option, error);
        // 只断言 "--" 出现对 "--" 这一例几乎不设防（任何含 "--" 的报错都成立），
        // 所以再钉住报错说的是"选项名不合法"，而不是别的原因。
        Assert.Contains("选项名", error);
    }

    [Fact]
    public void Bare_token_after_the_command_is_rejected()
        => Assert.Contains("stray", ParseError("pack", "stray"));

    [Fact]
    public void Find_unknown_option_reports_the_first_one()
    {
        ParsedCommandLine parsed = Parse("pack", "--input", "x", "--wat", "y", "--nope", "z");

        Assert.Equal("wat", parsed.FindUnknownOption(["input", "version", "output"]));
    }

    [Fact]
    public void Find_unknown_option_returns_null_when_all_are_known()
    {
        ParsedCommandLine parsed = Parse("pack", "--input", "x", "--version", "1.0");

        Assert.Null(parsed.FindUnknownOption(["input", "version"]));
    }
}
