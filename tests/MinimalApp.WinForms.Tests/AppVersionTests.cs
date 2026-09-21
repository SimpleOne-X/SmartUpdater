namespace SimpleOneX.SmartUpdater.Samples.WinForms.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2", "1.2")]
    // SourceLink / CI 可能追加 +提交哈希，必须截断后再解析。
    [InlineData("1.1.0+9a8b7c6", "1.1.0")]
    [InlineData("2.0.0+", "2.0.0")]
    public void Informational_version_is_parsed_verbatim_after_stripping_the_metadata(string informational, string expected)
    {
        Assert.True(AppVersion.TryParseInformational(informational, out Version version));
        // 逐字比较字符串而不是 Version 实例：1.1.0 与 1.1.0.0 必须可区分。
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("+1.0.0")]
    [InlineData("1")]
    public void Unparseable_informational_versions_are_rejected(string? informational)
    {
        Assert.False(AppVersion.TryParseInformational(informational, out _));
    }

    [Fact]
    public void Current_is_a_parseable_version()
    {
        // 测试程序集与示例程序集不同，所以只断言"能取到一个版本"，不钉具体数字。
        Assert.NotNull(AppVersion.Current);
        Assert.True(AppVersion.Current >= new Version(0, 0));
    }
}
