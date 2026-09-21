using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("has\ttab", "\"has\ttab\"")]
    public void Quote_only_wraps_when_needed(string input, string expected)
    {
        Assert.Equal(expected, CommandLine.Quote(input));
    }

    [Fact]
    public void Quote_escapes_embedded_double_quote()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", CommandLine.Quote("say \"hi\""));
    }

    [Fact]
    public void Backslash_not_adjacent_to_quote_is_left_alone()
    {
        // C:\Apps\MyApp 里的反斜杠不需要翻倍
        Assert.Equal(@"C:\Apps\MyApp", CommandLine.Quote(@"C:\Apps\MyApp"));
    }

    [Fact]
    public void Backslashes_before_embedded_quote_are_doubled()
    {
        // a\"b 里的反斜杠紧挨引号，必须翻倍再转义引号
        Assert.Equal("\"a\\\\\\\"b\"", CommandLine.Quote("a\\\"b"));
    }

    [Fact]
    public void Trailing_backslashes_are_doubled_when_wrapped()
    {
        // 路径以反斜杠结尾且需要加引号时，尾部反斜杠会紧挨闭合引号，必须翻倍，
        // 否则闭合引号会被转义掉，参数边界就错了
        Assert.Equal("\"C:\\Program Files\\\\\"", CommandLine.Quote(@"C:\Program Files\"));
    }

    [Fact]
    public void Trailing_backslashes_are_not_doubled_when_not_wrapped()
    {
        Assert.Equal(@"C:\Apps\", CommandLine.Quote(@"C:\Apps\"));
    }

    [Fact]
    public void Build_joins_arguments_with_single_spaces()
    {
        string line = CommandLine.Build(["--smartupdater-updated", "1.2.4", "--smartupdater-wait-pid", "4242"]);

        Assert.Equal("--smartupdater-updated 1.2.4 --smartupdater-wait-pid 4242", line);
    }

    [Fact]
    public void Build_quotes_arguments_containing_spaces()
    {
        string line = CommandLine.Build(["--path", @"C:\Program Files\MyApp\MyApp.exe"]);

        Assert.Equal("--path \"C:\\Program Files\\MyApp\\MyApp.exe\"", line);
    }

    [Fact]
    public void GetOptionValue_reads_the_token_after_the_option()
    {
        string[] args = ["--smartupdater-updated", "1.2.4", "--other", "x"];

        Assert.Equal("1.2.4", CommandLine.GetOptionValue(args, "--smartupdater-updated"));
    }

    [Fact]
    public void GetOptionValue_returns_null_when_option_is_absent()
    {
        string[] args = ["--other", "x"];

        Assert.Null(CommandLine.GetOptionValue(args, "--smartupdater-updated"));
    }

    [Fact]
    public void GetOptionValue_returns_null_when_value_is_missing()
    {
        string[] args = ["--smartupdater-updated"];

        Assert.Null(CommandLine.GetOptionValue(args, "--smartupdater-updated"));
    }

    [Fact]
    public void GetOptionValue_returns_null_when_next_token_is_another_option()
    {
        string[] args = ["--smartupdater-updated", "--smartupdater-rollback"];

        Assert.Null(CommandLine.GetOptionValue(args, "--smartupdater-updated"));
    }

    [Fact]
    public void Option_matching_is_case_insensitive()
    {
        string[] args = ["--SMARTUPDATER-UPDATED", "1.2.4"];

        Assert.Equal("1.2.4", CommandLine.GetOptionValue(args, "--smartupdater-updated"));
    }

    [Fact]
    public void HasFlag_detects_presence()
    {
        Assert.True(CommandLine.HasFlag(["--smartupdater-rollback"], "--smartupdater-rollback"));
        Assert.False(CommandLine.HasFlag(["--other"], "--smartupdater-rollback"));
    }

    [Fact]
    public void Argument_names_match_the_handover_protocol_literals()
    {
        // 跨进程协议靠字面量两端各写一遍就会静默失效，常量必须集中定义
        Assert.Equal("--smartupdater-updated", SmartUpdaterArgs.Updated);
        Assert.Equal("--smartupdater-wait-pid", SmartUpdaterArgs.WaitPid);
        Assert.Equal("--smartupdater-rollback", SmartUpdaterArgs.Rollback);
    }

    [Fact]
    public void Round_trip_through_windows_style_argument_splitting()
    {
        // 构造出来的命令行，被 Windows 解析回来后必须与原始参数逐个相等
        string[] original = [@"C:\Program Files\My App\x.exe", "plain", "with \"quote\"", @"ends\"];

        string line = CommandLine.Build(original);
        string[] parsed = CommandLineTestHelper.SplitLikeWindows(line);

        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("")]                             // 空串必须被引号包起来，否则参数会消失
    [InlineData("\"")]                           // 孤立引号
    [InlineData("a\"b")]                         // 引号前 0 个反斜杠
    [InlineData("a\\\"b")]                       // 引号前 1 个反斜杠
    [InlineData("a\\\\\"b")]                     // 引号前 2 个反斜杠
    [InlineData("a\\\\\\\"b")]                   // 引号前 3 个反斜杠
    [InlineData("has space\\")]                  // 需要加引号，尾部 1 个反斜杠
    [InlineData("has space\\\\")]                // 需要加引号，尾部 2 个反斜杠
    [InlineData("has space\\\\\\")]              // 需要加引号，尾部 3 个反斜杠
    [InlineData("no_space\\")]                   // 不需要加引号，尾部 1 个反斜杠
    [InlineData("no_space\\\\")]                 // 不需要加引号，尾部 2 个反斜杠
    [InlineData("\\\\\\")]                       // 纯反斜杠
    [InlineData("tab\there")]                    // 含 Tab
    [InlineData("\\\\server\\share\\my dir\\")]  // UNC 路径，含空格，结尾反斜杠
    [InlineData("say \"hi\" to \\\"you\\\"")]    // 引号与反斜杠混合
    public void Round_trip_preserves_boundary_arguments(string argument)
    {
        string[] expected = [argument];

        string line = CommandLine.Build(expected);
        string[] parsed = CommandLineTestHelper.SplitLikeWindows(line);

        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void GetOptionValue_requires_the_exact_option_name()
    {
        // 交接协议靠精确的选项名：只是前缀相同的另一个选项不能被当成它
        Assert.Null(CommandLine.GetOptionValue(["--smartupdater-updated-x", "1.0"], SmartUpdaterArgs.Updated));
    }

    [Fact]
    public void HasFlag_requires_the_exact_option_name()
    {
        Assert.False(CommandLine.HasFlag(["--smartupdater-rollback-x"], SmartUpdaterArgs.Rollback));
    }

    [Fact]
    public void HasFlag_matching_is_case_insensitive()
    {
        // 大小写不敏感是实现（OrdinalIgnoreCase）的既定行为，这里把它钉住
        Assert.True(CommandLine.HasFlag(["--SMARTUPDATER-ROLLBACK"], SmartUpdaterArgs.Rollback));
    }

    [Fact]
    public void GetOptionValue_matches_a_mixed_case_option_name()
    {
        Assert.Equal("1.2.3", CommandLine.GetOptionValue(["--SmartUpdater-Updated", "1.2.3"], SmartUpdaterArgs.Updated));
    }

    [Fact]
    public void GetOptionValue_returns_an_empty_value_as_is()
    {
        // 空串是合法的值，不能被当成“没有值”而返回 null
        Assert.Equal("", CommandLine.GetOptionValue(["--smartupdater-updated", ""], SmartUpdaterArgs.Updated));
    }
}
