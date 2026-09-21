using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>单个文件在升级时的处置策略。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FilePolicy>))]
public enum FilePolicy
{
    /// <summary>总是覆盖。</summary>
    Replace = 0,

    /// <summary>目标不存在时写入；已存在则保留使用者现有文件，且永不删除。</summary>
    Preserve = 1,
}

/// <summary>升级包内 <c>.smartupdater/manifest.json</c> 的模型。</summary>
public sealed class PackageManifest
{
    /// <summary>协议版本。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>本包对应的应用版本。</summary>
    [JsonConverter(typeof(VersionJsonConverter))]
    public required Version Version { get; init; }

    // 用 set 而非 init：源生成器反序列化 init 属性时，JSON 缺字段会丢弃初始值而得到 default（此处为 null）。
    /// <summary>包内全部文件，路径相对安装目录，使用正斜杠。</summary>
    public IReadOnlyList<ManifestFile> Files { get; set; } = [];
}

/// <summary>清单中的一个文件条目。</summary>
public sealed class ManifestFile
{
    /// <summary>相对安装目录的路径，正斜杠分隔。</summary>
    public required string Path { get; init; }

    /// <summary>文件内容的 SHA-256，十六进制小写。</summary>
    public required string Sha256 { get; init; }

    /// <summary>文件字节数。</summary>
    public long Size { get; init; }

    // 用 set 而非 init：源生成器反序列化 init 属性时，JSON 缺字段会丢弃初始值而得到 default。
    // 当前取值下 Replace 恰等于 default(FilePolicy)，这里的 set 只是防日后重排枚举值时丢掉缺省值。
    /// <summary>处置策略。缺省 <see cref="FilePolicy.Replace"/>。</summary>
    public FilePolicy Policy { get; set; } = FilePolicy.Replace;
}
