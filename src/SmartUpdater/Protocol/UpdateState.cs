namespace SimpleOneX.SmartUpdater;

/// <summary><c>.smartupdater/state.json</c> 的模型。全部属性用 set：源生成器反序列化 init 属性时会丢弃初始值。</summary>
internal sealed class UpdateState
{
    /// <summary>当前已安装版本；尚无记录时为 null。</summary>
    public Version? CurrentVersion { get; set; }

    /// <summary>设备唯一标识。<see cref="Guid.Empty"/> 表示尚未分配。</summary>
    public Guid DeviceGuid { get; set; }

    /// <summary>使用者明确跳过的版本。</summary>
    public List<Version> SkippedVersions { get; set; } = [];

    /// <summary>上次拿到的 feed ETag。</summary>
    public string? FeedETag { get; set; }

    /// <summary>上次检查更新的时间（UTC）。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>上次成功发送心跳的时间（UTC）。</summary>
    public DateTimeOffset? LastReportedAt { get; set; }

    /// <summary>连续"跳板升级"的次数（跨重启持久化）；本轮无可安装版本时归零。</summary>
    public int ChainLength { get; set; }

    /// <summary>触发跳板熔断时的 feed ETag；feed 不变则熔断持续生效。</summary>
    public string? ChainLimitFeedETag { get; set; }
}
