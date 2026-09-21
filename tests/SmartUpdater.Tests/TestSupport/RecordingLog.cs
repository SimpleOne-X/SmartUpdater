using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal sealed record LogEntry(UpdateLogLevel Level, UpdateStage? Stage, string Message, Exception? Exception);

/// <summary>把日志条目记在内存里，供测试断言"处置被记录了、失败没有被吞掉"。线程安全。</summary>
internal sealed class RecordingLog : IUpdateLog
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(level, stage, message, exception));
        }
    }

    public bool Contains(string fragment)
        => Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));

    public IEnumerable<LogEntry> AtLevel(UpdateLogLevel level)
        => Entries.Where(e => e.Level == level);
}
