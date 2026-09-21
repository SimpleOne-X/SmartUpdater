using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class WindowsFileNamesTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("NUL.tar.gz")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("COM¹")]
    [InlineData("com².txt")]
    [InlineData("LPT³")]
    [InlineData("CON .txt")]
    public void Reserved_device_names_are_detected(string segment)
    {
        Assert.True(WindowsFileNames.IsReservedDeviceName(segment));
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("COM")]
    [InlineData("LPT0")]
    [InlineData("COM¹¹")]
    [InlineData("COM⁴")]
    [InlineData("nul2")]
    [InlineData("MyApp.exe")]
    [InlineData(".con")]
    [InlineData("")]
    public void Ordinary_names_are_not_reserved(string segment)
    {
        Assert.False(WindowsFileNames.IsReservedDeviceName(segment));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("a*")]
    [InlineData("a?")]
    [InlineData("<a>")]
    [InlineData("\"a\"")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a\u0001b")]
    [InlineData("a\tb")]
    public void Invalid_characters_are_detected(string segment)
    {
        Assert.True(WindowsFileNames.HasInvalidCharacters(segment));
    }

    [Theory]
    [InlineData("a-b_c.d (1) [x]")]
    [InlineData("中文 名称.txt")]
    [InlineData("café")]
    [InlineData("")]
    public void Ordinary_segments_have_no_invalid_characters(string segment)
    {
        Assert.False(WindowsFileNames.HasInvalidCharacters(segment));
    }

    [Theory]
    [InlineData("a.", true)]
    [InlineData("a ", true)]
    [InlineData("a. ", true)]
    [InlineData(".a", false)]
    [InlineData(" a", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void Trailing_dot_or_space_is_detected(string segment, bool expected)
    {
        Assert.Equal(expected, WindowsFileNames.EndsWithDotOrSpace(segment));
    }
}
