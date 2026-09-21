using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateLogFactoryTests
{
    // 日志器还开着文件（写句柄，FileShare.ReadWrite）；File.ReadAllText 以 FileShare.Read 打开会撞上，所以共享读。
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Nothing_enabled_yields_the_null_log()
    {
        using InstallationFixture f = new();

        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(f.Layout, enableFileLogging: false, callback: null, new ManualTimeProvider());

        Assert.Same(NullUpdateLog.Instance, log);
        Assert.Null(fileLogger);
    }

    [Fact]
    public void File_logging_writes_information_but_not_debug()
    {
        using InstallationFixture f = new();
        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(f.Layout, enableFileLogging: true, callback: null, new ManualTimeProvider());
        using FileLogger disposable = fileLogger!;

        log.Write(UpdateLogLevel.Debug, null, "debug-line");
        log.Write(UpdateLogLevel.Information, UpdateStage.Check, "info-line");

        string content = ReadShared(fileLogger!.CurrentFilePath!);
        Assert.Contains("info-line", content, StringComparison.Ordinal);
        Assert.DoesNotContain("debug-line", content, StringComparison.Ordinal);
        Assert.StartsWith(f.Layout.LogDirectory, fileLogger.CurrentFilePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Callback_receives_every_level_including_debug()
    {
        using InstallationFixture f = new();
        var received = new List<(UpdateLogLevel Level, string Message)>();

        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(f.Layout, enableFileLogging: false, (level, message, _) => received.Add((level, message)), new ManualTimeProvider());

        log.Write(UpdateLogLevel.Debug, null, "d");
        log.Write(UpdateLogLevel.Error, UpdateStage.Commit, "e");

        Assert.Null(fileLogger);
        Assert.Equal([UpdateLogLevel.Debug, UpdateLogLevel.Error], received.Select(r => r.Level));
    }

    [Fact]
    public void Both_sinks_receive_the_same_line()
    {
        using InstallationFixture f = new();
        var received = new List<string>();
        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(f.Layout, enableFileLogging: true, (_, message, _) => received.Add(message), new ManualTimeProvider());
        using FileLogger disposable = fileLogger!;

        log.Write(UpdateLogLevel.Warning, UpdateStage.Download, "both");

        Assert.Contains("both", ReadShared(fileLogger!.CurrentFilePath!), StringComparison.Ordinal);
        Assert.Contains(received, m => m.Contains("both", StringComparison.Ordinal));
    }

    [Fact]
    public void A_throwing_callback_does_not_break_logging()
    {
        using InstallationFixture f = new();
        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(f.Layout, enableFileLogging: true, (_, _, _) => throw new InvalidOperationException("consumer bug"), new ManualTimeProvider());
        using FileLogger disposable = fileLogger!;

        log.Write(UpdateLogLevel.Information, null, "survives");

        Assert.Contains("survives", ReadShared(fileLogger!.CurrentFilePath!), StringComparison.Ordinal);
    }
}
