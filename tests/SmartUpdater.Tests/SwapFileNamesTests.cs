using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class SwapFileNamesTests
{
    // 后缀是落盘协议：崩溃残留的 .suold / .sunew 靠它在下一次启动时被认出来。
    // 其余测试都以 SwapFileNames.OldSuffix 这样的符号引用它，改了字面量它们照样绿，所以字面量只能在这里钉死。
    [Fact]
    public void Suffixes_and_search_patterns_are_the_on_disk_protocol()
    {
        Assert.Equal(".sunew", SwapFileNames.NewSuffix);
        Assert.Equal(".suold", SwapFileNames.OldSuffix);
        Assert.Equal("*.sunew", SwapFileNames.NewSearchPattern);
        Assert.Equal("*.suold", SwapFileNames.OldSearchPattern);
    }

    [Fact]
    public void NewPath_and_OldPath_append_the_suffix_to_the_whole_target_path()
    {
        Assert.Equal(@"C:\app\MyApp.exe.sunew", SwapFileNames.NewPath(@"C:\app\MyApp.exe"));
        Assert.Equal(@"C:\app\MyApp.exe.suold", SwapFileNames.OldPath(@"C:\app\MyApp.exe"));
    }

    [Theory]
    [InlineData("a.dll.sunew", true)]
    [InlineData("a.dll.suold", true)]
    [InlineData("A.DLL.SUNEW", true)]
    [InlineData("a.dll.SuOld", true)]
    [InlineData("a.dll", false)]
    [InlineData("a.sunew.dll", false)]
    [InlineData("sunew", false)]
    [InlineData("", false)]
    public void HasSwapSuffix_matches_either_suffix_ignoring_case(string fileName, bool expected)
    {
        Assert.Equal(expected, SwapFileNames.HasSwapSuffix(fileName));
    }
}
