using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class PhysicalFileOperationsTests
{
    private static readonly IFileOperations Fs = PhysicalFileOperations.Instance;

    [Fact]
    public void Delete_clears_read_only_attribute_first()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("ro.txt", "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Fs.Delete(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Delete_of_missing_file_or_missing_directory_is_a_no_op()
    {
        using var dir = new TempDirectory();

        Fs.Delete(dir.Resolve("nope.txt"));
        Fs.Delete(dir.Resolve("no-such-dir/nope.txt"));
    }

    [Fact]
    public void Move_with_overwrite_replaces_destination_and_removes_source()
    {
        using var dir = new TempDirectory();
        string a = dir.WriteFile("a.txt", "A");
        string b = dir.WriteFile("b.txt", "B");

        Fs.Move(a, b, overwrite: true);

        Assert.False(File.Exists(a));
        Assert.Equal("A", File.ReadAllText(b));
    }

    [Fact]
    public void Move_without_overwrite_onto_existing_destination_throws_IOException()
    {
        using var dir = new TempDirectory();
        string a = dir.WriteFile("a.txt", "A");
        string b = dir.WriteFile("b.txt", "B");

        Assert.Throws<IOException>(() => Fs.Move(a, b, overwrite: false));
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public void EnumerateFiles_recurses_and_filters_by_pattern()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.exe.suold", "");
        dir.WriteFile("sub/deep/b.dll.suold", "");
        dir.WriteFile("sub/c.dll.sunew", "");
        dir.WriteFile("plain.txt", "");

        IReadOnlyList<string> found = Fs.EnumerateFiles(dir.Root, "*.suold", recursive: true);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.EndsWith("a.exe.suold", StringComparison.Ordinal));
        Assert.Contains(found, p => p.EndsWith("b.dll.suold", StringComparison.Ordinal));
        Assert.Single(Fs.EnumerateFiles(dir.Root, "*.suold", recursive: false));
    }

    [Fact]
    public void EnumerateFiles_on_missing_directory_returns_empty()
    {
        using var dir = new TempDirectory();

        Assert.Empty(Fs.EnumerateFiles(dir.Resolve("missing"), "*", recursive: true));
    }

    [Fact]
    public void OpenWrite_truncates_and_OpenRead_reads_back()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("f.bin", "old content that is longer");

        using (Stream w = Fs.OpenWrite(file))
        {
            w.Write("new"u8);
            Fs.FlushToDisk(w);
        }

        using Stream r = Fs.OpenRead(file);
        using var reader = new StreamReader(r);
        Assert.Equal("new", reader.ReadToEnd());
        Assert.Equal(3, Fs.GetFileLength(file));
    }
}

/// <summary>
/// 拒绝 ListDirectory 的 ACE 只作用于本测试自己建的临时目录下的 locked 子目录，别的测试碰不到；
/// 因此不需要禁用并行；<see cref="PhysicalFileOperations"/> 的有界重试会吸收占用 / 拒绝访问的瞬时失败。
/// </summary>
public class PhysicalFileOperationsAclTests
{
    /// <summary>
    /// 启动恢复在 foreach 的头部调用递归枚举（位于各自的 try 之外）：一个无权访问的子目录若让枚举整个抛出，
    /// 整次恢复就会中止。无权的子目录必须被跳过，其余文件照常返回。
    /// </summary>
    [Fact]
    public void EnumerateFiles_skips_an_unreadable_subdirectory_instead_of_throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("需要 Windows ACL");
            return;
        }

        IFileOperations fs = PhysicalFileOperations.Instance;
        using var dir = new TempDirectory();
        dir.WriteFile("a.exe.suold", "");
        dir.WriteFile("open/b.dll.suold", "");
        dir.WriteFile("locked/c.dll.suold", "");

        // using 编译成 try/finally：断言失败也会先移除拒绝规则，外层的 dir 才删得掉（dir 先声明、后释放）。
        using (new DenyListDirectoryRule(dir.Resolve("locked")))
        {
            IReadOnlyList<string> found = fs.EnumerateFiles(dir.Root, "*.suold", recursive: true);

            Assert.Equal(2, found.Count);
            Assert.Contains(found, p => p.EndsWith("a.exe.suold", StringComparison.Ordinal));
            Assert.Contains(found, p => p.EndsWith("b.dll.suold", StringComparison.Ordinal));
        }

        Assert.Equal(3, fs.EnumerateFiles(dir.Root, "*.suold", recursive: true).Count);   // 规则移除后三个都回来，证明枚举本身没被改坏
    }

    /// <summary>对当前用户加一条拒绝 <c>ListDirectory</c> 的 ACE，Dispose 时移除；制造"递归枚举遇到无权子目录"。</summary>
    [SupportedOSPlatform("windows")]
    private sealed class DenyListDirectoryRule : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileSystemAccessRule _rule;

        public DenyListDirectoryRule(string directory)
        {
            _directory = new DirectoryInfo(directory);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            _rule = new FileSystemAccessRule(
                user,
                FileSystemRights.ListDirectory,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);

            DirectorySecurity acl = _directory.GetAccessControl();
            acl.AddAccessRule(_rule);
            _directory.SetAccessControl(acl);
        }

        public void Dispose()
        {
            DirectorySecurity acl = _directory.GetAccessControl();
            acl.RemoveAccessRule(_rule);
            _directory.SetAccessControl(acl);
        }
    }
}
