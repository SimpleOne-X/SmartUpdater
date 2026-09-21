using System.Diagnostics;

namespace SimpleOneX.SmartUpdater;

/// <summary>启动外部进程的缝。</summary>
internal interface IProcessLauncher
{
    /// <summary>启动进程并返回新进程 PID。</summary>
    int Start(string fileName, string arguments, string workingDirectory);
}

/// <summary>真实的进程启动器。</summary>
internal sealed class ProcessLauncher : IProcessLauncher
{
    private ProcessLauncher()
    {
    }

    public static ProcessLauncher Instance { get; } = new();

    public int Start(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {fileName}");
        return process.Id;
    }
}

/// <summary>等待进程退出的缝。</summary>
internal interface IProcessWaiter
{
    /// <summary>等待进程退出；true 表示已退出或进程不存在。</summary>
    bool WaitForExit(int processId, TimeSpan timeout);
}

/// <summary>真实的进程等待器。</summary>
internal sealed class ProcessWaiter : IProcessWaiter
{
    private ProcessWaiter()
    {
    }

    public static ProcessWaiter Instance { get; } = new();

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            return process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        }
    }
}
