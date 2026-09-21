using System.Security.Cryptography;
using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseSignatureTests
{
    // 协议示例的规范化形式：手写，不依赖实现。296 字节；SHA-256 由 openssl dgst 与 certutil 独立算出且一致。
    private const string SpecExampleCanonical =
        """{"version":"1.2.4","releasedAt":"2026-09-18T10:00:00.0000000Z","package":{"url":"packages/MyApp-1.2.4.zip","size":12345678,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},"minUpdatableFrom":"1.0.0","mode":"mandatory","rolloutPercent":100,"notes":"修复若干问题"}""";

    private const string SpecExampleCanonicalSha256 = "b3c5fe963b4e349d9d8f4a93b9944bde995592723db107decbe026706f70a4b5";

    // 已知答案测试用的公钥与签名。私钥不入库：签名由 openssl 生成，这里只保留公钥和签名结果。
    private const string TestPublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEU2KaZUBSStFPqGXB2mPLaVsaT/QMwgPFIuPq7RkHV4dyr8N7hjGJnwOOFoEMEkSMuheQ1RvNNw/zYt7viWsidw==";

    // openssl dgst -sha256 -sign key-sec1.pem canonical.json | base64
    private const string SpecExampleSignature =
        "MEYCIQD5NFOrC5Om63eT4Omr/JZYt+pL9hkO/CSJb+1uK5de5gIhAMgCPv1QDNO/E/4XWwKGI4yxBtvvXRJwU5qEfbfZQ5YL";

    /// <summary>每次运行现生成一把 P-256 密钥，导出 SEC1 与 PKCS#8 两种 PEM，并附上用 BCL 独立算出的公钥。</summary>
    public static TheoryData<string, string> GeneratedPemEncodings()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string expectedPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return new TheoryData<string, string>
        {
            { key.ExportECPrivateKeyPem(), expectedPublicKey },
            { key.ExportPkcs8PrivateKeyPem(), expectedPublicKey },
        };
    }

    private static ReleaseEntry SpecExample(string? signature = null) => new()
    {
        Version = new Version(1, 2, 4),
        ReleasedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        Package = new PackageInfo
        {
            Url = "packages/MyApp-1.2.4.zip",
            Size = 12345678,
            Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        },
        MinUpdatableFrom = new Version(1, 0, 0),
        Mode = UpdateMode.Mandatory,
        RolloutPercent = 100,
        Notes = "修复若干问题",
        Signature = signature,
    };

    private static ReleaseEntry With(ReleaseEntry e, Action<MutableEntry> change)
    {
        var m = new MutableEntry(e);
        change(m);
        return m.Build();
    }

    [Fact]
    public void Canonical_form_of_the_spec_example_matches_the_hand_written_bytes()
    {
        byte[] canonical = ReleaseSignature.Canonicalize(SpecExample());

        Assert.Equal(SpecExampleCanonical, Encoding.UTF8.GetString(canonical));
        Assert.Equal(296, canonical.Length);
        Assert.Equal(SpecExampleCanonicalSha256, Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    [Fact]
    public void Canonical_form_escapes_strings_and_omits_null_fields()
    {
        var entry = new ReleaseEntry
        {
            Version = new Version(2, 0),
            ReleasedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)).AddTicks(1234567),
            Package = new PackageInfo
            {
                Url = "packages/App 2.0.zip",
                Size = 0,
                Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            },
            MinUpdatableFrom = null,
            Mode = UpdateMode.Optional,
            RolloutPercent = 5,
            Notes = "A \"quoted\" \\ back <tag> & 修复\nline2\u0001\u001f\t",
            Signature = "ignored",
        };

        string canonical = Encoding.UTF8.GetString(ReleaseSignature.Canonicalize(entry));

        // 期望串按规范化规则手工推导：+08:00 换成 UTC、null 字段省略、只转义 " \ 与控制字符（小写十六进制）、其余原样。
        Assert.Equal(
            """{"version":"2.0","releasedAt":"2026-01-01T19:04:05.1234567Z","package":{"url":"packages/App 2.0.zip","size":0,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},"mode":"optional","rolloutPercent":5,"notes":"A \"quoted\" \\ back <tag> & 修复\nline2\u0001\u001f\t"}""",
            canonical);
    }

    [Fact]
    public void Canonical_form_uses_short_escapes_for_backspace_form_feed_and_carriage_return()
    {
        ReleaseEntry entry = With(SpecExample(), m => m.Notes = "a\bb\fc\rd");

        string canonical = Encoding.UTF8.GetString(ReleaseSignature.Canonicalize(entry));

        // \b \f \r 必须写成短转义，而不是 \u0008 \u000c \u000d
        Assert.EndsWith(",\"notes\":\"a\\bb\\fc\\rd\"}", canonical);
    }

    [Fact]
    public void Null_notes_omits_the_key_while_empty_notes_keeps_it()
    {
        string withoutNotes = Encoding.UTF8.GetString(ReleaseSignature.Canonicalize(With(SpecExample(), m => m.Notes = null)));
        string emptyNotes = Encoding.UTF8.GetString(ReleaseSignature.Canonicalize(With(SpecExample(), m => m.Notes = "")));

        Assert.EndsWith(",\"rolloutPercent\":100}", withoutNotes);
        Assert.DoesNotContain("notes", withoutNotes);
        Assert.EndsWith(",\"rolloutPercent\":100,\"notes\":\"\"}", emptyNotes);
    }

    [Fact]
    public void Undefined_update_mode_is_rejected_by_canonicalize()
    {
        // 强转放在方法体内：public 测试方法的参数里不放非法枚举值（CS0051 与可读性）
        ReleaseEntry entry = With(SpecExample(), m => m.Mode = (UpdateMode)7);

        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseSignature.Canonicalize(entry));
    }

    [Fact]
    public void Signature_field_never_participates_in_the_canonical_form()
    {
        Assert.Equal(ReleaseSignature.Canonicalize(SpecExample()), ReleaseSignature.Canonicalize(SpecExample("anything")));
    }

    [Fact]
    public void Openssl_produced_signature_verifies_against_the_fixed_public_key()
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);

        Assert.True(ReleaseSignature.Verify(SpecExample(SpecExampleSignature), publicKey));
    }

    public static TheoryData<string, Action<MutableEntry>> FieldTampering => new()
    {
        { "version", m => m.Version = new Version(1, 2, 5) },
        { "releasedAt", m => m.ReleasedAt = m.ReleasedAt.AddSeconds(1) },
        { "url", m => m.Url = "packages/MyApp-1.2.4-evil.zip" },
        { "size", m => m.Size += 1 },
        { "sha256", m => m.Sha256 = "f" + m.Sha256[1..] },
        { "minUpdatableFrom", m => m.MinUpdatableFrom = new Version(1, 0, 1) },
        { "minUpdatableFrom→null", m => m.MinUpdatableFrom = null },
        { "mode", m => m.Mode = UpdateMode.Optional },
        { "rolloutPercent", m => m.RolloutPercent = 99 },
        { "notes", m => m.Notes = "修复若干问题。" },
        { "notes→null", m => m.Notes = null },
    };

    [Theory]
    [MemberData(nameof(FieldTampering))]
    public void Tampering_any_signed_field_fails_verification(string field, Action<MutableEntry> tamper)
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);
        ReleaseEntry tampered = With(SpecExample(SpecExampleSignature), tamper);

        Assert.False(ReleaseSignature.Verify(tampered, publicKey), $"篡改 {field} 后验签仍通过");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("AQID")]
    [InlineData("MEYCIQD5NFOrC5Om63eT4Omr/JZYt+pL9hkO/CSJb+1uK5de5gIhAMgCPv1QDNO/E/4XWwKGI4yxBtvvXRJwU5qEfbfZQ5YK")]
    public void Malformed_or_wrong_signature_yields_false_not_an_exception(string? signature)
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);

        Assert.False(ReleaseSignature.Verify(SpecExample(signature), publicKey));
    }

    [Fact]
    public void Truncated_signature_yields_false()
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);
        string truncated = Convert.ToBase64String(Convert.FromBase64String(SpecExampleSignature)[..40]);

        Assert.False(ReleaseSignature.Verify(SpecExample(truncated), publicKey));
    }

    [Fact]
    public void Signature_from_a_different_key_fails()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);
        string signature = ReleaseSignature.Sign(SpecExample(), other);

        Assert.False(ReleaseSignature.Verify(SpecExample(signature), publicKey));
    }

    [Fact]
    public void Sign_then_verify_round_trips_with_a_fresh_key_and_der_encoding()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa publicOnly = ReleaseSignature.ImportPublicKey(ReleaseSignature.ExportPublicKey(key));

        string signature = ReleaseSignature.Sign(SpecExample(), key);
        byte[] der = Convert.FromBase64String(signature);

        Assert.InRange(der.Length, 68, 72);                                  // DER：r、s 各 32 字节，按首位与前导零情况各占 33–35 字节
        Assert.Equal(0x30, der[0]);                                          // DER SEQUENCE
        Assert.True(ReleaseSignature.Verify(SpecExample(signature), publicOnly));
    }

    [Theory]
    [MemberData(nameof(GeneratedPemEncodings))]
    public void Both_pem_encodings_import_and_sign_for_the_matching_public_key(string pem, string expectedPublicKey)
    {
        using ECDsa privateKey = ReleaseSignature.ImportPrivateKeyPem(pem);
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(expectedPublicKey);

        string signature = ReleaseSignature.Sign(SpecExample(), privateKey);

        Assert.True(ReleaseSignature.Verify(SpecExample(signature), publicKey));
        Assert.Equal(expectedPublicKey, ReleaseSignature.ExportPublicKey(privateKey));
    }

    [Fact]
    public void Non_p256_keys_are_rejected_on_import()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<CryptographicException>(() => ReleaseSignature.ImportPrivateKeyPem(p384.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<CryptographicException>(() => ReleaseSignature.ImportPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Sign_rejects_a_non_p256_private_key()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<CryptographicException>(() => ReleaseSignature.Sign(SpecExample(), p384));
    }

    [Fact]
    public void Garbage_key_material_throws_the_documented_exceptions()
    {
        using RSA rsa = RSA.Create(2048);

        Assert.Throws<FormatException>(() => ReleaseSignature.ImportPublicKey("not base64!!"));
        Assert.Throws<CryptographicException>(() => ReleaseSignature.ImportPublicKey(Convert.ToBase64String(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 })));
        Assert.Throws<ArgumentException>(() => ReleaseSignature.ImportPrivateKeyPem("not a pem"));
        Assert.Throws<CryptographicException>(() => ReleaseSignature.ImportPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void Verify_computes_over_the_original_version_string_not_a_normalized_one()
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(TestPublicKeySpki);
        ReleaseEntry fourPart = With(SpecExample(SpecExampleSignature), m => m.Version = new Version(1, 2, 4, 0));

        Assert.False(ReleaseSignature.Verify(fourPart, publicKey));   // "1.2.4.0" ≠ "1.2.4"：版本归一化必须放在验签之后
    }

    /// <summary>ReleaseEntry 的属性是 init-only，测试用它构造篡改后的副本。</summary>
    public sealed class MutableEntry(ReleaseEntry source)
    {
        public Version Version { get; set; } = source.Version;

        public DateTimeOffset ReleasedAt { get; set; } = source.ReleasedAt;

        public string Url { get; set; } = source.Package.Url;

        public long Size { get; set; } = source.Package.Size;

        public string Sha256 { get; set; } = source.Package.Sha256;

        public Version? MinUpdatableFrom { get; set; } = source.MinUpdatableFrom;

        public UpdateMode Mode { get; set; } = source.Mode;

        public int RolloutPercent { get; set; } = source.RolloutPercent;

        public string? Notes { get; set; } = source.Notes;

        public string? Signature { get; set; } = source.Signature;

        public ReleaseEntry Build() => new()
        {
            Version = Version,
            ReleasedAt = ReleasedAt,
            Package = new PackageInfo { Url = Url, Size = Size, Sha256 = Sha256 },
            MinUpdatableFrom = MinUpdatableFrom,
            Mode = Mode,
            RolloutPercent = RolloutPercent,
            Notes = Notes,
            Signature = Signature,
        };
    }
}
