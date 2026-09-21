using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>前提：运行中的 exe 与已加载的 dll 可以改名并在原路径写入新文件。只在 Windows 上运行。</summary>
public class RunningProcessSwapTests
{
    private static readonly IFileOperations Physical = PhysicalFileOperations.Instance;

    [Fact]
    public void Running_executable_is_swapped_and_rolled_back_while_it_keeps_running()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "需要 Windows 的文件锁语义与 System32\\waitfor.exe");
        string waitfor = Path.Combine(Environment.SystemDirectory, "waitfor.exe");
        Assert.SkipUnless(File.Exists(waitfor), "缺少 waitfor.exe");

        using var dir = new TempDirectory();
        string exe = dir.Resolve("app.exe");
        File.Copy(waitfor, exe);
        File.WriteAllBytes(dir.Resolve("app.exe.sunew"), [0x4D, 0x5A, 0x00, 0x01]);
        var ops = new List<SwapOperation> { new(SwapKind.Write, exe, HadTarget: true) };
        var swapper = new FileSwapper(Physical, new RecordingLog());

        using var process = Process.Start(new ProcessStartInfo(exe, $"/T 60 SmartUpdaterTest{Guid.NewGuid():N}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            if (process.WaitForExit(300))
            {
                Assert.Fail($"waitfor 应当阻塞等待信号，实际退出码 {process.ExitCode}");
            }

            swapper.Commit(ops);

            Assert.False(process.HasExited);
            Assert.Equal(new byte[] { 0x4D, 0x5A, 0x00, 0x01 }, File.ReadAllBytes(exe));
            Assert.True(File.Exists(exe + SwapFileNames.OldSuffix));
            // 锁在文件内容上而不是目录项上：运行中的旧镜像不能删，但刚才已经成功改名了
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(exe + SwapFileNames.OldSuffix));

            Assert.Empty(swapper.Rollback(ops));
            Assert.Empty(swapper.DeletePendingFiles(ops));

            Assert.False(process.HasExited);
            Assert.Equal(new FileInfo(waitfor).Length, new FileInfo(exe).Length);
            Assert.False(File.Exists(exe + SwapFileNames.OldSuffix));
            Assert.False(File.Exists(exe + SwapFileNames.NewSuffix));
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // 只忽略 Kill 自己的失败（进程恰好刚退出等），不能让它顶替 try 里的原始断言失败。
                }

                process.WaitForExit(5000);
            }
        }
    }

    [Fact]
    public void Loaded_native_library_is_swapped_while_loaded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "需要 Windows 的文件锁语义与 System32\\version.dll");
        string source = Path.Combine(Environment.SystemDirectory, "version.dll");
        Assert.SkipUnless(File.Exists(source), "缺少 version.dll");

        using var dir = new TempDirectory();
        string dll = dir.Resolve("lib.dll");
        File.Copy(source, dll);
        File.WriteAllBytes(dir.Resolve("lib.dll.sunew"), [1, 2, 3]);
        var ops = new List<SwapOperation> { new(SwapKind.Write, dll, HadTarget: true) };

        IntPtr handle = NativeLibrary.Load(dll);
        try
        {
            new FileSwapper(Physical, new RecordingLog()).Commit(ops);

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dll));
            Assert.True(File.Exists(dll + SwapFileNames.OldSuffix));
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(dll + SwapFileNames.OldSuffix));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        File.Delete(dll + SwapFileNames.OldSuffix);
        Assert.False(File.Exists(dll + SwapFileNames.OldSuffix));
    }
}
