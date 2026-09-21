using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

internal enum JournalStatus
{
    Missing,
    Valid,
    Corrupt,
}

internal readonly record struct JournalReadResult(JournalStatus Status, UpdateJournal? Journal);

/// <summary>journal.json 的读写。转移只允许 preparing → committing → done，且以磁盘上的内容为准。</summary>
internal sealed class JournalStore
{
    private readonly IFileOperations _fs;
    private readonly IUpdateLog _log;

    public JournalStore(string journalFilePath, IFileOperations fs, IUpdateLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFilePath);
        FilePath = journalFilePath;
        _fs = fs;
        _log = log;
    }

    public string FilePath { get; }

    /// <summary>读取并校验 journal：不存在 → Missing，无法解析 → Corrupt（记 Error），否则 Valid。<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> 原样向上传播。</summary>
    public JournalReadResult Read()
    {
        try
        {
            UpdateJournal? journal = AtomicFile.ReadJson(_fs, FilePath, SmartUpdaterJsonContext.Default.UpdateJournal);
            if (journal is null)
            {
                return _fs.FileExists(FilePath)
                    ? Corrupt(null)
                    : new JournalReadResult(JournalStatus.Missing, null);
            }

            return new JournalReadResult(JournalStatus.Valid, journal);
        }
        catch (JsonException ex)
        {
            return Corrupt(ex);
        }
    }

    public void Write(UpdateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        JournalReadResult current = Read();
        if (current.Status == JournalStatus.Corrupt)
        {
            throw new InvalidOperationException("journal 已损坏，必须先运行恢复。");
        }

        string previous = current.Journal is { } existing ? Literal(existing.State) : "(无)";

        bool allowed = journal.State switch
        {
            JournalState.Preparing => current.Status == JournalStatus.Missing,
            JournalState.Committing => current.Journal is { State: JournalState.Preparing } p && p.ToVersion.Equals(journal.ToVersion),
            JournalState.Done => current.Journal is { State: JournalState.Committing } c && c.ToVersion.Equals(journal.ToVersion),
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"journal 不允许从 {previous} 转移到 {Literal(journal.State)}（目标版本 {journal.ToVersion}）。");
        }

        string? directory = Path.GetDirectoryName(FilePath);
        if (directory is not null && !_fs.DirectoryExists(directory))
        {
            _fs.CreateDirectory(directory);
        }

        AtomicFile.WriteJson(_fs, FilePath, journal, SmartUpdaterJsonContext.Default.UpdateJournal);
        _log.Information(UpdateStage.Commit, $"journal: {previous} → {Literal(journal.State)} ({journal.FromVersion?.ToString() ?? "(首次安装)"} → {journal.ToVersion})");
    }

    /// <summary>连同残留的 .tmp 一起删除。文件本来就不存在也不抛异常，且照样记一条"journal 已删除"。IO 异常原样传播。</summary>
    public void Delete()
    {
        // 连同可能残留的 .tmp 一起删：崩溃于原子写的 Move 之前会留下它，恢复结束后不该有任何事务痕迹
        _fs.Delete(FilePath);
        _fs.Delete(FilePath + AtomicFile.TempSuffix);
        _log.Information(UpdateStage.Commit, "journal 已删除");
    }

    private JournalReadResult Corrupt(Exception? ex)
    {
        _log.Error(UpdateStage.Commit, $"journal 无法解析：{FilePath}", ex);
        return new JournalReadResult(JournalStatus.Corrupt, null);
    }

    private static string Literal(JournalState state) => state switch
    {
        JournalState.Preparing => "preparing",
        JournalState.Committing => "committing",
        JournalState.Done => "done",
        _ => state.ToString(),
    };
}
