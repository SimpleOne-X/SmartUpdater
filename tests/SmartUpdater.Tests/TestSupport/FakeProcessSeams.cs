using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal sealed record Launch(string FileName, string Arguments, string WorkingDirectory);

internal sealed class FakeProcessLauncher : IProcessLauncher
{
    public List<Launch> Launches { get; } = [];

    public Exception? ThrowOnStart { get; set; }

    public int NextPid { get; set; } = 4242;

    public int Start(string fileName, string arguments, string workingDirectory)
    {
        Launches.Add(new Launch(fileName, arguments, workingDirectory));
        if (ThrowOnStart is not null)
        {
            throw ThrowOnStart;
        }

        return NextPid++;
    }
}

internal sealed class FakeProcessWaiter : IProcessWaiter
{
    public List<(int Pid, TimeSpan Timeout)> Calls { get; } = [];

    public bool Result { get; set; } = true;

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Calls.Add((processId, timeout));
        return Result;
    }
}
