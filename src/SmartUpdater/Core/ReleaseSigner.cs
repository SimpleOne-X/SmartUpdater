using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 发布条目的签名入口：服务端可以直接在代码里给 feed 条目签名，规范化与客户端验签完全一致。
/// 只支持 ECDSA P-256；签名结果填进新条目的 <c>Signature</c>（或 JSON 的 <c>signature</c> 字段），<see cref="ReleaseEntry.Signature"/> 是 init 属性，不能事后赋值。
/// </summary>
public static class ReleaseSigner
{
    /// <summary>对条目的规范化 JSON 签名，返回 base64（DER）签名，填进新条目的 <c>Signature</c>（或 JSON 的 <c>signature</c> 字段）；条目上已有的 Signature 字段不参与签名。</summary>
    /// <param name="entry">待签名的条目。</param>
    /// <param name="privateKey">P-256 私钥。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> 或 <paramref name="privateKey"/> 为 null。</exception>
    /// <exception cref="CryptographicException"><paramref name="privateKey"/> 不是 P-256，或不含私钥。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entry"/> 的 Mode 不是已定义的 <see cref="UpdateMode"/> 值。</exception>
    public static string Sign(ReleaseEntry entry, ECDsa privateKey)
        => ReleaseSignature.Sign(entry, privateKey);

    /// <summary>用公钥验证条目上的签名，与客户端验签使用同一套规范化；签名缺失、格式错误或不匹配都返回 false，不抛异常。</summary>
    /// <param name="entry">带 <see cref="ReleaseEntry.Signature"/> 的条目。</param>
    /// <param name="publicKey">验证用的公钥。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> 或 <paramref name="publicKey"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">条目带有可解码的签名，但 Mode 不是已定义的 <see cref="UpdateMode"/> 值。</exception>
    public static bool Verify(ReleaseEntry entry, ECDsa publicKey)
        => ReleaseSignature.Verify(entry, publicKey);

    /// <summary>从 PEM 文本导入 P-256 私钥，PKCS#8 与 SEC1 两种格式都接受。</summary>
    /// <remarks>返回的 <see cref="ECDsa"/> 由调用方 Dispose。</remarks>
    /// <param name="pem">私钥的 PEM 文本。</param>
    /// <exception cref="ArgumentNullException"><paramref name="pem"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="pem"/> 是空白、不含可识别的 PEM 密钥、含多个密钥，或是加密的 PEM。</exception>
    /// <exception cref="CryptographicException">密钥不是 EC 密钥、不是 P-256，或 PEM 里只有公钥而没有私钥。</exception>
    public static ECDsa ImportPrivateKeyPem(string pem)
        => ReleaseSignature.ImportPrivateKeyPem(pem);

    /// <summary>导出公钥：SubjectPublicKeyInfo 的 base64 单行文本，即 <see cref="UpdateClientOptions.PublicKey"/> 的格式。</summary>
    /// <param name="key">要导出公钥的密钥。</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 null。</exception>
    public static string ExportPublicKey(ECDsa key)
        => ReleaseSignature.ExportPublicKey(key);
}
