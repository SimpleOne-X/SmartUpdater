using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>一条上报记录。时机、内容与补报队列由包负责；怎么送出去由 <c>IUpdateReporter</c> 负责。</summary>
public sealed class UpdateReport
{
    /// <summary>事件类型。</summary>
    public required UpdateEventType EventType { get; init; }

    /// <summary>设备唯一标识，首次启动生成并持久化于 state.json。</summary>
    public required Guid DeviceGuid { get; init; }

    /// <summary>机器名，仅作可读名。</summary>
    public string? MachineName { get; init; }

    /// <summary>失败发生的阶段；Updated / Heartbeat 时为 null。</summary>
    public UpdateStage? Stage { get; init; }

    /// <summary>更新前版本。</summary>
    [JsonConverter(typeof(NullableVersionJsonConverter))]
    public Version? FromVersion { get; init; }

    /// <summary>更新后（或目标）版本。</summary>
    [JsonConverter(typeof(NullableVersionJsonConverter))]
    public Version? ToVersion { get; init; }

    /// <summary>本次事件是否成功。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>失败原因。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>本轮耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>安装盘可用字节数。</summary>
    public long? DiskFreeBytes { get; init; }

    /// <summary>操作系统版本。</summary>
    public string? OsVersion { get; init; }

    /// <summary>上报时间（UTC）。</summary>
    public required DateTimeOffset ReportedAt { get; init; }

    /// <summary>仅 Failed 时附带的日志尾部，上限 8 KB。</summary>
    public string? LogTail { get; init; }
}
