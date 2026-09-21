using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>feed（releases.json）反序列化后的文档。</summary>
public sealed class ReleaseFeedDocument
{
    /// <summary>协议版本。客户端遇到不认识的大版本应拒绝处理并上报。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>通道名，当前仅用于标识，未参与挑选逻辑。</summary>
    public string? Channel { get; init; }

    /// <summary>服务端下发的客户端运行参数。为 null 时客户端沿用本地配置。</summary>
    public ClientPolicy? Client { get; init; }

    // 用 set 而非 init：源生成器反序列化 init 属性时，JSON 缺字段会丢弃初始值而得到 default（此处为 null）。
    /// <summary>发布条目，按版本降序。</summary>
    public IReadOnlyList<ReleaseEntry> Releases { get; set; } = [];

    /// <summary>
    /// <see cref="ReleaseFeedReader"/> 逐条容错时跳过的条目与原因（如 "releases[2]: …"）。
    /// 由 <see cref="ReleaseFeedValidator"/> 记入日志；不参与序列化（非 public 成员）。
    /// </summary>
    internal IReadOnlyList<string> ParseWarnings { get; init; } = [];
}

/// <summary>单个发布条目。</summary>
public sealed class ReleaseEntry
{
    /// <summary>本条目的版本号。</summary>
    [JsonConverter(typeof(VersionJsonConverter))]
    public required Version Version { get; init; }

    /// <summary>发布时间（UTC）。</summary>
    public DateTimeOffset ReleasedAt { get; init; }

    /// <summary>升级包的位置与校验信息。</summary>
    public required PackageInfo Package { get; init; }

    /// <summary>低于此版本的客户端不能直接升到本条目，须走阶梯升级。</summary>
    [JsonConverter(typeof(NullableVersionJsonConverter))]
    public Version? MinUpdatableFrom { get; init; }

    // 用 set 而非 init：源生成器反序列化 init 属性时，JSON 缺字段会丢弃初始值而得到 default。
    // 当前取值下 Optional 恰等于 default(UpdateMode)，这里的 set 只是防日后重排枚举值时丢掉缺省值。
    /// <summary>更新模式。缺省为 <see cref="UpdateMode.Optional"/>。</summary>
    public UpdateMode Mode { get; set; } = UpdateMode.Optional;

    // 用 set 而非 init：同上，否则 JSON 缺字段时得到 0 而不是 100。
    /// <summary>灰度百分比 0~100。缺省 100 表示全量。</summary>
    public int RolloutPercent { get; set; } = 100;

    /// <summary>更新说明，Optional 模式下由消费方展示给用户。</summary>
    public string? Notes { get; init; }

    /// <summary>对本条目规范化 JSON 的 ECDSA P-256 签名，base64。</summary>
    public string? Signature { get; init; }
}

/// <summary>升级包的位置与校验信息。</summary>
public sealed class PackageInfo
{
    /// <summary>相对 feed 的相对路径，或绝对 URL。</summary>
    public required string Url { get; init; }

    /// <summary>包的字节数。</summary>
    public long Size { get; init; }

    /// <summary>包的 SHA-256，十六进制小写。必填，缺失即拒绝安装。</summary>
    public required string Sha256 { get; init; }
}

/// <summary>服务端可下发的客户端运行参数。客户端对每一项都有硬下限保护。</summary>
public sealed class ClientPolicy
{
    /// <summary>轮询间隔（秒）。</summary>
    public int? PollIntervalSeconds { get; init; }

    /// <summary>抖动窗口（秒）。</summary>
    public int? JitterWindowSeconds { get; init; }

    /// <summary>心跳上报间隔（秒）。</summary>
    public int? HeartbeatIntervalSeconds { get; init; }
}
