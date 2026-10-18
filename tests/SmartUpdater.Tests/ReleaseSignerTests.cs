using System.Security.Cryptography;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseSignerTests
{
    [Fact]
    public void Sign_produces_a_signature_the_client_verifier_accepts()
    {
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa key = ReleaseSigner.ImportPrivateKeyPem(generated.ExportPkcs8PrivateKeyPem());

        string signature = ReleaseSigner.Sign(ReleaseSignatureTests.SpecExample(), key);

        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(ReleaseSigner.ExportPublicKey(key));
        Assert.True(ReleaseSignature.Verify(ReleaseSignatureTests.SpecExample(signature), publicKey));
    }

    [Fact]
    public void Verify_accepts_the_openssl_known_answer()
    {
        using ECDsa publicKey = ReleaseSignature.ImportPublicKey(ReleaseSignatureTests.TestPublicKeySpki);

        Assert.True(ReleaseSigner.Verify(ReleaseSignatureTests.SpecExample(ReleaseSignatureTests.SpecExampleSignature), publicKey));
    }

    [Fact]
    public void Verify_rejects_a_changed_signed_field()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ReleaseEntry signed = ReleaseSignatureTests.SpecExample(ReleaseSigner.Sign(ReleaseSignatureTests.SpecExample(), key));
        ReleaseEntry changed = new ReleaseSignatureTests.MutableEntry(signed) { Notes = "改过" }.Build();

        Assert.True(ReleaseSigner.Verify(signed, key));   // 对照：未改动的条目能过，下面的 false 才说明是改动导致
        Assert.False(ReleaseSigner.Verify(changed, key));
    }

    [Fact]
    public void Sign_ignores_an_existing_signature_field()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        string signature = ReleaseSigner.Sign(ReleaseSignatureTests.SpecExample("junk"), key);

        Assert.True(ReleaseSigner.Verify(ReleaseSignatureTests.SpecExample(signature), key));
    }

    [Fact]
    public void ImportPrivateKeyPem_rejects_a_p384_key()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string pem = p384.ExportPkcs8PrivateKeyPem();

        Assert.Throws<CryptographicException>(() => ReleaseSigner.ImportPrivateKeyPem(pem));
    }

    [Fact]
    public void ImportPrivateKeyPem_rejects_a_public_key_pem()
    {
        using ECDsa k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = k.ExportSubjectPublicKeyInfoPem();

        Assert.Throws<CryptographicException>(() => ReleaseSigner.ImportPrivateKeyPem(publicPem));
    }

    [Fact]
    public void ExportPublicKey_matches_the_bcl_spki()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), ReleaseSigner.ExportPublicKey(key));
    }
}
