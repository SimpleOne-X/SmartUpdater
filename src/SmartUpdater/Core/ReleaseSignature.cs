using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 发布条目的规范化 JSON 与 ECDSA P-256 签名。规范化形式逐条写死（字段顺序、空白、数字与日期表示、转义），
/// 两端、跨版本、跨语言都能一字不差地复现；签名为 SHA-256 + ECDSA，DER 编码（与 OpenSSL / X.509 一致），base64 传输。
/// </summary>
internal static class ReleaseSignature
{
    public const string CurveOid = "1.2.840.10045.3.1.7";

    public static byte[] Canonicalize(ReleaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new StringBuilder(256);
        builder.Append("{\"version\":");
        AppendString(builder, entry.Version.ToString());
        builder.Append(",\"releasedAt\":");
        AppendString(builder, entry.ReleasedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(",\"package\":{\"url\":");
        AppendString(builder, entry.Package.Url);
        builder.Append(",\"size\":").Append(entry.Package.Size.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"sha256\":");
        AppendString(builder, entry.Package.Sha256);
        builder.Append('}');

        if (entry.MinUpdatableFrom is { } floor)
        {
            builder.Append(",\"minUpdatableFrom\":");
            AppendString(builder, floor.ToString());
        }

        builder.Append(",\"mode\":");
        AppendString(builder, entry.Mode switch
        {
            UpdateMode.Optional => "optional",
            UpdateMode.Mandatory => "mandatory",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Mode, "未知的 UpdateMode"),
        });
        builder.Append(",\"rolloutPercent\":").Append(entry.RolloutPercent.ToString(CultureInfo.InvariantCulture));

        if (entry.Notes is { } notes)
        {
            builder.Append(",\"notes\":");
            AppendString(builder, notes);
        }

        builder.Append('}');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static string Sign(ReleaseEntry entry, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        EnsureP256(privateKey);
        byte[] signature = privateKey.SignData(Canonicalize(entry), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(ReleaseEntry entry, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (string.IsNullOrWhiteSpace(entry.Signature))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(entry.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        // VerifyData 对非法 DER / 长度不对的输入返回 false 而不抛；再兜一层 CryptographicException 以防平台差异
        try
        {
            return publicKey.VerifyData(Canonicalize(entry), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static ECDsa ImportPublicKey(string spkiBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiBase64);
        byte[] der = Convert.FromBase64String(spkiBase64);   // FormatException 原样抛出
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out _);
            EnsureP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static ECDsa ImportPrivateKeyPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);   // 非 PEM → ArgumentException；非 EC 密钥 → CryptographicException
            EnsureP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static void EnsureP256(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        string? oid = parameters.Curve.Oid?.Value;
        if (!parameters.Curve.IsNamed || !string.Equals(oid, CurveOid, StringComparison.Ordinal))
        {
            throw new CryptographicException($"只支持 NIST P-256（{CurveOid}）曲线，实际为 '{oid ?? "(未命名曲线)"}'。");
        }
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
