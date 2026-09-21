namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>sign 表单的原始输入。空串表示"没填"。</summary>
internal sealed record SignForm
{
    /// <summary>要签名的 releases.json。</summary>
    public string FeedPath { get; init; } = string.Empty;

    /// <summary>ECDSA P-256 私钥 PEM 文件。</summary>
    public string KeyPath { get; init; } = string.Empty;

    /// <summary>是否连已有签名的条目一起重签。</summary>
    public bool Resign { get; init; }
}
