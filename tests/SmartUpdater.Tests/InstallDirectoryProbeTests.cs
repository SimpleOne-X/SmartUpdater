using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class InstallDirectoryProbeTests
{
    [Fact]
    public void Writable_directory_is_reported_writable_and_leaves_no_probe_file()
    {
        using var dir = new TempDirectory();
        string state = dir.Resolve("app/.smartupdater");

        InstallDirectoryProbeResult result = InstallDirectoryProbe.Probe(state);

        Assert.True(result.IsWritable);
        Assert.Null(result.FailureReason);
        Assert.True(Directory.Exists(state));
        Assert.Empty(Directory.EnumerateFiles(state, InstallDirectoryProbe.ProbeFilePrefix + "*"));
    }

    [Fact]
    public void Path_that_is_a_file_is_reported_not_writable_without_throwing()
    {
        using var dir = new TempDirectory();
        string file = dir.WriteFile("app/.smartupdater", "I am a file, not a directory");

        InstallDirectoryProbeResult result = InstallDirectoryProbe.Probe(file);

        Assert.False(result.IsWritable);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0name")]
    public void Malformed_path_is_reported_not_writable_without_throwing(string malformed)
    {
        InstallDirectoryProbeResult result = InstallDirectoryProbe.Probe(malformed);

        Assert.False(result.IsWritable);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public void Directory_with_a_deny_acl_is_reported_not_writable_and_names_the_reason()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("需要 Windows ACL");
            return;
        }

        using var dir = new TempDirectory();
        string state = dir.Resolve("app/.smartupdater");
        Directory.CreateDirectory(state);

        // using 编译成 try/finally：断言失败也会先移除拒绝规则，再由外层 dir 删除临时目录（dir 先声明、后释放）。
        using (new DenyCreateFilesRule(state))
        {
            InstallDirectoryProbeResult result = InstallDirectoryProbe.Probe(state);

            Assert.False(result.IsWritable);
            Assert.Contains(nameof(UnauthorizedAccessException), result.FailureReason, StringComparison.Ordinal);
        }

        Assert.True(InstallDirectoryProbe.Probe(state).IsWritable);   // 规则移除后恢复可写，也证明探针没留下残余
    }

    /// <summary>对当前用户加一条拒绝 CreateFiles/CreateDirectories 的 ACE，Dispose 时移除。目录的 ReadOnly 属性不阻止建文件，所以只能这样造"不可写"。</summary>
    [SupportedOSPlatform("windows")]
    private sealed class DenyCreateFilesRule : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileSystemAccessRule _rule;

        public DenyCreateFilesRule(string directory)
        {
            _directory = new DirectoryInfo(directory);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            _rule = new FileSystemAccessRule(
                user,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
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
