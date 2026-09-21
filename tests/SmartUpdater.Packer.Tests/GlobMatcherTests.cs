namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    // 字面匹配
    [InlineData("appsettings.json", "appsettings.json", true)]
    [InlineData("appsettings.json", "appsettings.Development.json", false)]
    // 大小写不敏感（Windows 语义）
    [InlineData("AppSettings.json", "appsettings.json", true)]
    // * 不跨段
    [InlineData("*.json", "appsettings.json", true)]
    [InlineData("*.json", "config/appsettings.json", false)]
    [InlineData("config/*.json", "config/appsettings.json", true)]
    [InlineData("config/*.json", "config/sub/appsettings.json", false)]
    [InlineData("appsettings*.json", "appsettings.Development.json", true)]
    // ? 恰好一个字符、不跨段
    [InlineData("data?.db", "data1.db", true)]
    [InlineData("data?.db", "data12.db", false)]
    [InlineData("data?.db", "data.db", false)]
    [InlineData("a?b", "a/b", false)]
    // ** 匹配零或多段
    [InlineData("config/**", "config/a.json", true)]
    [InlineData("config/**", "config/sub/deep/a.json", true)]
    [InlineData("config/**", "config", false)]
    [InlineData("**/*.db", "a.db", true)]
    [InlineData("**/*.db", "data/a.db", true)]
    [InlineData("**/*.db", "data/sub/a.db", true)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/x/y/c", false)]
    // . 不是元字符
    [InlineData("a.b", "axb", false)]
    public void Matches_follows_the_documented_semantics(string pattern, string path, bool expected)
    {
        Assert.True(GlobMatcher.IsValidPattern(pattern, out string? error), error);
        Assert.Equal(expected, GlobMatcher.Matches(pattern, path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("config\\a.json")]
    [InlineData("a**b")]
    [InlineData("**b")]
    [InlineData("a**")]
    [InlineData("*.{json,xml}")]
    [InlineData("data[0-9].db")]
    // 空段：连续的 /、开头的 /、结尾的 /
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("config/")]
    [InlineData("/")]
    [InlineData("a/**/")]
    // . 与 .. 段：路径里永远不会出现，写了就是永远不命中
    [InlineData("./a")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("../a")]
    [InlineData("..")]
    // : 在 Windows 文件名里非法，盘符或 ADS 写法永远不会命中
    [InlineData("C:/x")]
    [InlineData("a:b")]
    public void Invalid_patterns_are_rejected(string pattern)
    {
        Assert.False(GlobMatcher.IsValidPattern(pattern, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("config/")]
    [InlineData("./a")]
    [InlineData("a/../b")]
    [InlineData("C:/x")]
    public void Rejection_messages_for_path_shape_errors_name_the_offending_pattern(string pattern)
    {
        Assert.False(GlobMatcher.IsValidPattern(pattern, out string? error));

        Assert.Contains(pattern, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_slash_error_tells_the_user_to_write_a_directory_as_dir_double_star()
    {
        Assert.False(GlobMatcher.IsValidPattern("config/", out string? error));

        Assert.Contains("config/**", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b.json")]
    [InlineData("a/**")]
    [InlineData("**/*.db")]
    [InlineData("a/**/b")]
    [InlineData("*.json")]
    [InlineData("my.config")]
    [InlineData("...")]
    public void Ordinary_patterns_are_still_valid(string pattern)
    {
        Assert.True(GlobMatcher.IsValidPattern(pattern, out string? error), error);
        Assert.Null(error);
    }

    [Fact]
    public void Regex_metacharacters_in_the_pattern_are_matched_literally()
    {
        Assert.True(GlobMatcher.IsValidPattern("a+b(c).json", out _));
        Assert.True(GlobMatcher.Matches("a+b(c).json", "a+b(c).json"));
        Assert.False(GlobMatcher.Matches("a+b(c).json", "aab(c).json"));
    }

    [Fact]
    public void A_trailing_newline_in_the_path_does_not_match()
    {
        // 正则的 $ 会在结尾换行符之前匹配；整串锚定必须用 \z，否则 "a.json\n" 会被当成 "a.json"。
        Assert.True(GlobMatcher.Matches("a.json", "a.json"));
        Assert.False(GlobMatcher.Matches("a.json", "a.json\n"));
    }
}
