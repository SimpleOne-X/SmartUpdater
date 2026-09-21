using System.Diagnostics;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ProcessSeamsTests
{
    [Fact]
    public void WaitForExit_returns_true_immediately_for_a_process_that_does_not_exist()
    {
        // 先起一个立刻结束的子进程，拿到一个"确实曾存在、现在已退出"的 PID
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows 专用：用 cmd.exe 制造已退出的进程"); return; }
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("exit 0");
        int pid;
        using (Process child = Process.Start(psi)!)
        {
            Assert.True(child.WaitForExit(10_000));
            pid = child.Id;
        }

        var sw = Stopwatch.StartNew();
        bool exited = ProcessWaiter.Instance.WaitForExit(pid, TimeSpan.FromSeconds(30));

        Assert.True(exited);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"应立即返回，实际 {sw.Elapsed}");
    }

    [Fact]
    public void WaitForExit_returns_false_when_the_process_is_still_running()
    {
        bool exited = ProcessWaiter.Instance.WaitForExit(Environment.ProcessId, TimeSpan.FromMilliseconds(50));

        Assert.False(exited);
    }

    [Fact]
    public void ProcessLauncher_starts_the_process_and_returns_its_pid()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows 专用"); return; }

        int pid = ProcessLauncher.Instance.Start("cmd.exe", "/c exit 7", Path.GetTempPath());

        Assert.True(pid > 0);
        Assert.True(ProcessWaiter.Instance.WaitForExit(pid, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ProcessLauncher_propagates_start_failures()
    {
        Assert.ThrowsAny<Exception>(() => ProcessLauncher.Instance.Start(
            Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N") + ".exe"), "", Path.GetTempPath()));
    }
}
