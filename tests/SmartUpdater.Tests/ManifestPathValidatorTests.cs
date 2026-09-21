using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ManifestPathValidatorTests
{
    private static ManifestFile File(string path) => new() { Path = path, Sha256 = new string('a', 64), Size = 1 };

    [Fact]
    public void Validate_reports_a_null_element_instead_of_throwing()
    {
        IReadOnlyList<string> errors = ManifestPathValidator.Validate([File("ok.txt"), null!, File("ok2.txt")]);

        Assert.Single(errors);
    }

    [Theory]
    [InlineData("", "路径为空")]
    [InlineData("   ", "路径为空")]
    [InlineData(@"a\b", "含反斜杠")]
    [InlineData(@"C:\x", "含反斜杠")]
    [InlineData("/abs", "绝对路径")]
    [InlineData("a//b", "空路径段")]
    [InlineData("a/", "空路径段")]
    [InlineData("./a", "含 . 或 .. 段")]
    [InlineData("a/./b", "含 . 或 .. 段")]
    [InlineData("../a", "含 . 或 .. 段")]
    [InlineData("a/../b", "含 . 或 .. 段")]
    [InlineData("a/../../b", "含 . 或 .. 段")]
    [InlineData("C:/x", "含非法字符")]
    [InlineData("a:stream", "含非法字符")]
    [InlineData("a|b", "含非法字符")]
    [InlineData("a*", "含非法字符")]
    [InlineData("a?", "含非法字符")]
    [InlineData("a<b", "含非法字符")]
    [InlineData("a>b", "含非法字符")]
    [InlineData("a\"b", "含非法字符")]
    [InlineData("a\u0001", "含非法字符")]
    [InlineData("a.", "路径段以点或空格结尾")]
    [InlineData("a. ", "路径段以点或空格结尾")]
    [InlineData("dir./x", "路径段以点或空格结尾")]
    [InlineData("a ", "路径段以点或空格结尾")]
    [InlineData("CON", "保留设备名")]
    [InlineData("con.txt", "保留设备名")]
    [InlineData("sub/NUL", "保留设备名")]
    [InlineData("COM1.log", "保留设备名")]
    [InlineData(".smartupdater/manifest.json", "位于更新器状态目录")]
    [InlineData(".SmartUpdater/x", "位于更新器状态目录")]
    [InlineData(".smartupdater", "位于更新器状态目录")]
    [InlineData("a.sunew", "使用了更新器保留后缀")]
    [InlineData("a.SUOLD", "使用了更新器保留后缀")]
    [InlineData("x.suold/y", "使用了更新器保留后缀")]
    public void Unsafe_paths_are_rejected_with_the_first_violated_rule(string path, string expectedReason)
    {
        bool safe = ManifestPathValidator.IsSafeRelativePath(path, out string? reason);

        Assert.False(safe);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("MyApp.exe")]
    [InlineData("Resources/logo.png")]
    [InlineData("a-b_c (1) [x].txt")]
    [InlineData("中文/目录/文件.dll")]
    [InlineData(".config")]
    [InlineData("sub/.hidden")]
    [InlineData("COM10")]
    [InlineData("CONSOLE.txt")]
    [InlineData("smartupdater/x")]
    [InlineData("a.sunew.txt")]
    [InlineData("deep/a/b/c/d.txt")]
    [InlineData("docs/.smartupdater-notes.txt")]
    public void Safe_paths_are_accepted(string path)
    {
        Assert.True(ManifestPathValidator.IsSafeRelativePath(path, out string? reason), reason);
        Assert.Null(reason);
    }

    // 攻击样本：每行都是真实的 zip-slip / Windows 路径规范化技巧，reason 断言的是"第一条被违反的规则"。
    [Theory]
    // 反斜杠穿越与 UNC / 设备命名空间前缀
    [InlineData(@"..\..\Windows\System32\evil.dll", "含反斜杠")]
    [InlineData(@"\\server\share\x", "含反斜杠")]
    [InlineData(@"\\?\C:\x", "含反斜杠")]
    [InlineData(@"\\.\PhysicalDrive0", "含反斜杠")]
    // 正斜杠形式的绝对路径
    [InlineData("//server/share/x", "绝对路径")]
    [InlineData("/etc/passwd", "绝对路径")]
    // 深层穿越、末尾的 ..
    [InlineData("a/b/../../../c", "含 . 或 .. 段")]
    [InlineData("a/..", "含 . 或 .. 段")]
    [InlineData("..", "含 . 或 .. 段")]
    [InlineData(".", "含 . 或 .. 段")]
    // Win32 会剥掉段尾的空格与点，".. " 在磁盘上就是 ".."："含 . 或 .. 段"规则只认精确的 ".."，必须靠"段尾空格或点"规则挡住
    [InlineData(".. /x", "路径段以点或空格结尾")]
    [InlineData("../ /x", "含 . 或 .. 段")]
    [InlineData("a/.. ", "路径段以点或空格结尾")]
    [InlineData("...", "路径段以点或空格结尾")]
    [InlineData("a/.../b", "路径段以点或空格结尾")]
    // 盘符相对路径、NTFS 备用数据流
    [InlineData("C:x", "含非法字符")]
    [InlineData("a.txt:hidden", "含非法字符")]
    [InlineData("a.txt::$DATA", "含非法字符")]
    [InlineData("dir:ads/file", "含非法字符")]
    // 通配符与控制字符（含 NUL、换行、制表）
    [InlineData("*.dll", "含非法字符")]
    [InlineData("a?.dll", "含非法字符")]
    [InlineData("a\tb", "含非法字符")]
    [InlineData("a\nb", "含非法字符")]
    [InlineData("a\0b", "含非法字符")]
    [InlineData("a\u001fb", "含非法字符")]
    // 保留设备名：全部 4 个名字、COM/LPT 端点、目录段、带扩展名、大小写
    [InlineData("nul.txt", "保留设备名")]
    [InlineData("Aux", "保留设备名")]
    [InlineData("prn.dll", "保留设备名")]
    [InlineData("LPT1", "保留设备名")]
    [InlineData("COM9.txt", "保留设备名")]
    [InlineData("NUL/x", "保留设备名")]
    [InlineData("a/CON/b", "保留设备名")]
    [InlineData("Con.tar.gz", "保留设备名")]
    // 规则顺序：设备名后面补点或空格时，先命中"段尾空格或点"规则
    [InlineData("AUX.", "路径段以点或空格结尾")]
    [InlineData("CON ", "路径段以点或空格结尾")]
    // 状态目录：大小写、以及状态目录后面再嵌套
    [InlineData(".SMARTUPDATER/journal.json", "位于更新器状态目录")]
    [InlineData(".smartupdater/sub/a.txt", "位于更新器状态目录")]
    [InlineData(".smartupdater.", "路径段以点或空格结尾")]
    // 更新器保留后缀：文件、目录段、混合大小写
    [InlineData("MyApp.exe.sunew", "使用了更新器保留后缀")]
    [InlineData("a/b.SuNew", "使用了更新器保留后缀")]
    [InlineData("dir.suold/file.txt", "使用了更新器保留后缀")]
    // 仅空白
    [InlineData("\t", "路径为空")]
    [InlineData(" \r\n ", "路径为空")]
    public void Attack_samples_are_rejected(string path, string expectedReason)
    {
        bool safe = ManifestPathValidator.IsSafeRelativePath(path, out string? reason);

        Assert.False(safe);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Null_path_is_reported_as_empty_instead_of_throwing()
    {
        bool safe = ManifestPathValidator.IsSafeRelativePath(null!, out string? reason);

        Assert.False(safe);
        Assert.Equal("路径为空", reason);
    }

    [Theory]
    // 我们不做 URL 解码：百分号编码的 ".." 只是一个普通文件名
    [InlineData("%2e%2e/x")]
    [InlineData("a%2fb")]
    // 名字里带点、空格但不在段尾
    [InlineData("a b.txt")]
    [InlineData("a..b")]
    [InlineData("..a")]
    [InlineData("...a/b")]
    // 像设备名但不是
    [InlineData("NULL.txt")]
    [InlineData("CONFIG")]
    [InlineData("AUX1")]
    // 像状态目录 / 保留后缀但不是
    [InlineData("a/.smartupdater/x")]
    [InlineData(".smartupdater2/x")]
    [InlineData("sunew")]
    [InlineData("a.sun")]
    public void Look_alike_but_legal_paths_are_accepted(string path)
    {
        Assert.True(ManifestPathValidator.IsSafeRelativePath(path, out string? reason), reason);
        Assert.Null(reason);
    }

    [Fact]
    public void Validate_reports_every_offending_path_and_reason()
    {
        IReadOnlyList<string> errors = ManifestPathValidator.Validate([File("ok.dll"), File("../evil"), File("CON"), File(@"a\b")]);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("../evil", StringComparison.Ordinal) && e.Contains("含 . 或 .. 段", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("CON", StringComparison.Ordinal) && e.Contains("保留设备名", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(@"a\b", StringComparison.Ordinal) && e.Contains("含反斜杠", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_duplicates_ignoring_case()
    {
        IReadOnlyList<string> errors = ManifestPathValidator.Validate([File("Lib/A.dll"), File("lib/a.dll")]);

        string error = Assert.Single(errors);
        Assert.Contains("重复路径", error, StringComparison.Ordinal);
        Assert.Contains("lib/a.dll", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_same_file_name_in_different_directories()
    {
        Assert.Empty(ManifestPathValidator.Validate([File("a.txt"), File("b/a.txt"), File("c/d/a.txt")]));
    }

    [Fact]
    public void Validate_of_empty_list_is_valid()
    {
        Assert.Empty(ManifestPathValidator.Validate([]));
    }
}
