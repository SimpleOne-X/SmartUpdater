using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateLogSinksTests
{
    [Fact]
    public void Level_extensions_write_the_matching_level_stage_message_and_exception()
    {
        var log = new RecordingLog();
        var boom = new IOException("boom");

        log.Debug(UpdateStage.Download, "d");
        log.Information(null, "i");
        log.Warning(UpdateStage.Verify, "w", boom);
        log.Error(UpdateStage.Commit, "e", boom);

        Assert.Equal(
            [
                new LogEntry(UpdateLogLevel.Debug, UpdateStage.Download, "d", null),
                new LogEntry(UpdateLogLevel.Information, null, "i", null),
                new LogEntry(UpdateLogLevel.Warning, UpdateStage.Verify, "w", boom),
                new LogEntry(UpdateLogLevel.Error, UpdateStage.Commit, "e", boom),
            ],
            log.Entries);
    }

    [Fact]
    public void NullUpdateLog_swallows_everything()
    {
        NullUpdateLog.Instance.Error(UpdateStage.Commit, "ignored", new IOException("boom"));
        NullUpdateLog.Instance.Write(UpdateLogLevel.Debug, null, "ignored");
    }

    [Fact]
    public void CallbackLog_prefixes_the_stage_and_passes_the_exception()
    {
        var received = new List<(UpdateLogLevel Level, string Message, Exception? Exception)>();
        var log = new CallbackLog((level, message, ex) => received.Add((level, message, ex)));
        var boom = new IOException("boom");

        log.Write(UpdateLogLevel.Error, UpdateStage.Commit, "write failed", boom);
        log.Write(UpdateLogLevel.Information, null, "no stage");

        Assert.Equal((UpdateLogLevel.Error, "[Commit] write failed", (Exception?)boom), received[0]);
        Assert.Equal((UpdateLogLevel.Information, "no stage", (Exception?)null), received[1]);
    }

    [Fact]
    public void Throwing_callback_does_not_propagate()
    {
        var log = new CallbackLog((_, _, _) => throw new InvalidOperationException("consumer bug"));

        log.Write(UpdateLogLevel.Information, null, "x");
    }

    [Fact]
    public void CompositeLog_fans_out_and_isolates_a_failing_sink()
    {
        var first = new RecordingLog();
        var second = new RecordingLog();
        var failing = new CallbackLog((_, _, _) => throw new InvalidOperationException("consumer bug"));
        IUpdateLog throwingSink = new ThrowingLog();

        var composite = new CompositeLog(first, failing, throwingSink, second);
        composite.Write(UpdateLogLevel.Warning, UpdateStage.Verify, "hash mismatch");

        Assert.Equal("hash mismatch", Assert.Single(first.Entries).Message);
        Assert.Equal("hash mismatch", Assert.Single(second.Entries).Message);
        Assert.Equal(UpdateStage.Verify, second.Entries[0].Stage);
    }

    private sealed class ThrowingLog : IUpdateLog
    {
        public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
            => throw new InvalidOperationException("sink bug");
    }
}
