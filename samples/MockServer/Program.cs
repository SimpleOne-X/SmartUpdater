namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>模拟生产服务器的入口。</summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (!MockServerOptions.TryParse(args, out MockServerOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("用法: MockServer --root <dir> [--port <n>]");
            return 1;
        }

        await using var host = new MockServerHost(options!);
        await host.StartAsync(CancellationToken.None);

        // 机器可读的一行，端到端脚本靠 grep 它拿到端口。必须是第一行 stdout。
        Console.WriteLine(MockServerHost.FormatListeningLine(host.BaseAddress));
        Console.Out.Flush();

        await host.WaitForShutdownAsync(CancellationToken.None);
        return 0;
    }
}
