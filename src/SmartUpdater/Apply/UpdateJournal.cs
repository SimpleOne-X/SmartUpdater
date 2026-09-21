using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>更新事务的三个状态，只能按 Preparing → Committing → Done 前进。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<JournalState>))]
internal enum JournalState
{
    /// <summary>正在写 .sunew，尚未触碰任何现存文件。</summary>
    Preparing = 0,

    /// <summary>正在改名提交。</summary>
    Committing = 1,

    /// <summary>全部改名完成，等待新版本启动后清理。</summary>
    Done = 2,
}

/// <summary>journal 里的一条写入记录。</summary>
internal sealed class JournalEntry
{
    /// <summary>相对安装目录的路径，正斜杠分隔。</summary>
    public required string Path { get; init; }

    /// <summary>preparing 时目标是否已存在。回滚据此区分"新文件（删掉）"与"被替换的文件（从 .suold 移回）"。</summary>
    public bool HadTarget { get; init; }
}

/// <summary><c>.smartupdater/journal.json</c> 的模型，更新事务的唯一真相源。</summary>
internal sealed class UpdateJournal
{
    /// <summary>事务状态。</summary>
    public JournalState State { get; set; }

    /// <summary>更新前版本；首次安装为 null。</summary>
    public Version? FromVersion { get; set; }

    /// <summary>目标版本。</summary>
    public required Version ToVersion { get; set; }

    /// <summary>要写入（新增或替换）的文件，已按提交顺序排好。</summary>
    public List<JournalEntry> Writes { get; set; } = [];

    /// <summary>要删除的文件，已按提交顺序排好。</summary>
    public List<string> Deletes { get; set; } = [];

    /// <summary>事务开始时间（UTC）。</summary>
    public DateTimeOffset StartedAt { get; set; }
}
