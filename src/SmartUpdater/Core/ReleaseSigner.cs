using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 发布条目的签名入口：服务端可以直接在代码里给 feed 条目签名，规范化与客户端验签完全一致。
/// 只支持 ECDSA P-256；签名结果写进 <see cref="ReleaseEntry.Signature"/>。
/// </summary>
public static class ReleaseSigner
{
    /// <summary>对条目的规范化 JSON 签名，返回 base64（DER）签名，写进 <see cref="ReleaseEntry.Signature"/>；条目上已有的 Signature 字段不参与签名。</summary>
    /// <param name="entry">待签名的条目。</param>
    /// <param name="privateKey">P-256 私钥；非 P-256 抛 <see cref="CryptographicException"/>。</param>
    public static string Sign(ReleaseEntry entry, ECDsa privateKey)
        => ReleaseSignature.Sign(entry, privateKey);

    /// <summary>用公钥验证条目上的签名，与客户端验签使用同一套规范化；签名缺失、格式错误或不匹配都返回 false。</summary>
    /// <param name="entry">带 <see cref="ReleaseEntry.Signature"/> 的条目。</param>
    /// <param name="publicKey">验证用的公钥。</param>
    public static bool Verify(ReleaseEntry entry, ECDsa publicKey)
        => ReleaseSignature.Verify(entry, publicKey);

    /// <summary>从 PEM 文本导入 P-256 私钥，PKCS#8 与 SEC1 两种格式都接受；非 P-256 抛 <see cref="CryptographicException"/>。</summary>
    /// <remarks>返回的 <see cref="ECDsa"/> 由调用方 Dispose。</remarks>
    /// <param name="pem">私钥的 PEM 文本。</param>
    public static ECDsa ImportPrivateKeyPem(string pem)
        => ReleaseSignature.ImportPrivateKeyPem(pem);

    /// <summary>导出公钥：SubjectPublicKeyInfo 的 base64 单行文本，即 <see cref="UpdateClientOptions.PublicKey"/> 的格式。</summary>
    /// <param name="key">要导出公钥的密钥。</param>
    public static string ExportPublicKey(ECDsa key)
        => ReleaseSignature.ExportPublicKey(key);
}
