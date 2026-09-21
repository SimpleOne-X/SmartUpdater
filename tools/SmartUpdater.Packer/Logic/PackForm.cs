namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>pack 表单的原始输入。全部是界面上的文本，未经校验；空串表示"没填"。</summary>
internal sealed record PackForm
{
    /// <summary>已 publish 的应用目录。</summary>
    public string InputDirectory { get; init; } = string.Empty;

    /// <summary>发布目录（写 packages/ 与 releases.json）。</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>四段版本号，如 1.2.4.0。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>zip 文件名前缀；空则由 pack 从输入目录里唯一的 exe 推导。</summary>
    public string PackageName { get; init; } = string.Empty;

    /// <summary>feed 的渠道；空则由 pack 取默认值。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>是否强制更新（mode=mandatory）；否则是可选更新。</summary>
    public bool IsMandatory { get; init; }

    /// <summary>灰度百分比文本；空则由 pack 取默认值 100。</summary>
    public string RolloutPercent { get; init; } = string.Empty;

    /// <summary>阶梯升级门槛版本；空则不写。</summary>
    public string MinUpdatableFrom { get; init; } = string.Empty;

    /// <summary>更新说明；空则不写。</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>preserve 模式，每行一个；空行忽略。</summary>
    public string Preserve { get; init; } = string.Empty;

    /// <summary>是否允许替换同版本但内容不同的条目（--force）。</summary>
    public bool Force { get; init; }
}
