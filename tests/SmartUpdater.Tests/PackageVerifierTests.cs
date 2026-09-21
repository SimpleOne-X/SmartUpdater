using System.Security.Cryptography;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class PackageVerifierTests
{
    private static readonly byte[] Package = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7 % 256)).ToArray();

    private sealed class Collector : IProgress<long>
    {
        public List<long> Reports { get; } = [];
        public void Report(long value) => Reports.Add(value);
    }

    private static (TempDirectory Temp, string Path, ReleaseEntry Release) Stage(string? sha256 = null, long? size = null, string? signature = null)
    {
        var temp = new TempDirectory();
        temp.WriteBytes("1.2.4.0.zip", Package);
        ReleaseEntry release = FeedFixtures.Release("1.2.4", size: size ?? Package.Length, sha256: sha256 ?? FeedFixtures.Sha256Hex(Package), notes: "修复", signature: signature);
        return (temp, temp.Resolve("1.2.4.0.zip"), release);
    }

    private static (ECDsa Key, string PublicKey) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, ReleaseSignature.ExportPublicKey(key));
    }

    private static ReleaseEntry Signed(ReleaseEntry release, ECDsa key) => new()
    {
        Version = release.Version,
        ReleasedAt = release.ReleasedAt,
        Package = release.Package,
        MinUpdatableFrom = release.MinUpdatableFrom,
        Mode = release.Mode,
        RolloutPercent = release.RolloutPercent,
        Notes = release.Notes,
        Signature = ReleaseSignature.Sign(release, key),
    };

    [Fact]
    public void Matching_size_and_hash_pass_without_a_public_key_and_log_the_hash()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        using (temp)
        {
            var log = new RecordingLog();

            PackageVerifier.Verify(release, path, null, log, null, CancellationToken.None);

            Assert.True(log.Contains(FeedFixtures.Sha256Hex(Package)));
            Assert.All(log.Entries, e => Assert.Equal(UpdateStage.Verify, e.Stage));
        }
    }

    [Fact]
    public void Uppercase_expected_hash_is_accepted()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage(sha256: FeedFixtures.Sha256Hex(Package).ToUpperInvariant());
        using (temp)
        {
            PackageVerifier.Verify(release, path, null, new RecordingLog(), null, CancellationToken.None);
        }
    }

    [Fact]
    public void Size_mismatch_is_rejected_before_hashing()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage(size: Package.Length + 1);
        using (temp)
        {
            var progress = new Collector();

            var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(release, path, null, new RecordingLog(), progress, CancellationToken.None));

            Assert.Equal(UpdateStage.Verify, ex.Stage);
            Assert.Contains((Package.Length + 1).ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Contains(Package.Length.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Empty(progress.Reports);
        }
    }

    [Fact]
    public void Hash_mismatch_is_rejected_with_both_hashes_in_the_message_and_logged_as_error()
    {
        string wrong = FeedFixtures.Sha256Hex([1, 2, 3]);
        (TempDirectory temp, string path, ReleaseEntry release) = Stage(sha256: wrong);
        using (temp)
        {
            var log = new RecordingLog();

            var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(release, path, null, log, null, CancellationToken.None));

            Assert.Equal(UpdateStage.Verify, ex.Stage);
            Assert.Contains(wrong, ex.Message, StringComparison.Ordinal);
            Assert.Contains(FeedFixtures.Sha256Hex(Package), ex.Message, StringComparison.Ordinal);
            Assert.Contains(log.AtLevel(UpdateLogLevel.Error), e => e.Message.Contains(wrong, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Missing_file_is_rejected_as_verify_failure()
    {
        using var temp = new TempDirectory();
        ReleaseEntry release = FeedFixtures.Release("1.2.4", size: 10);

        var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(release, temp.Resolve("missing.zip"), null, new RecordingLog(), null, CancellationToken.None));

        Assert.Equal(UpdateStage.Verify, ex.Stage);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void Progress_reports_cumulative_bytes_up_to_the_file_size()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        using (temp)
        {
            var progress = new Collector();

            PackageVerifier.Verify(release, path, null, new RecordingLog(), progress, CancellationToken.None);

            Assert.True(progress.Reports.Count >= 2);
            Assert.Equal(Package.Length, progress.Reports[^1]);
            Assert.Equal(progress.Reports.OrderBy(b => b), progress.Reports);
        }
    }

    [Fact]
    public void Valid_signature_passes_when_a_public_key_is_configured()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        (ECDsa key, string publicKey) = NewKey();
        using (temp)
        using (key)
        {
            PackageVerifier.Verify(Signed(release, key), path, publicKey, new RecordingLog(), null, CancellationToken.None);
        }
    }

    [Fact]
    public void Missing_signature_is_rejected_when_a_public_key_is_configured()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        (ECDsa key, string publicKey) = NewKey();
        using (temp)
        using (key)
        {
            var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(release, path, publicKey, new RecordingLog(), null, CancellationToken.None));

            Assert.Equal(UpdateStage.Verify, ex.Stage);
            Assert.Contains("签名", ex.Message, StringComparison.Ordinal);
            Assert.Contains("缺少", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tampered_entry_fails_signature_verification()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        (ECDsa key, string publicKey) = NewKey();
        using (temp)
        using (key)
        {
            ReleaseEntry signed = Signed(release, key);
            var tampered = new ReleaseEntry
            {
                Version = signed.Version,
                ReleasedAt = signed.ReleasedAt,
                Package = signed.Package,
                Mode = signed.Mode,
                RolloutPercent = signed.RolloutPercent,
                Notes = "改过的说明",
                Signature = signed.Signature,
            };

            var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(tampered, path, publicKey, new RecordingLog(), null, CancellationToken.None));

            Assert.Equal(UpdateStage.Verify, ex.Stage);
        }
    }

    [Fact]
    public void Signature_from_another_key_is_rejected()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        (ECDsa signer, _) = NewKey();
        (ECDsa other, string otherPublicKey) = NewKey();
        using (temp)
        using (signer)
        using (other)
        {
            Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(Signed(release, signer), path, otherPublicKey, new RecordingLog(), null, CancellationToken.None));
        }
    }

    [Fact]
    public void Garbage_signature_is_ignored_when_no_public_key_is_configured()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage(signature: "not-even-base64");
        using (temp)
        {
            PackageVerifier.Verify(release, path, null, new RecordingLog(), null, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Invalid_public_key_is_a_verify_failure(string publicKey)
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage(signature: "AAAA");
        using (temp)
        {
            var ex = Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(release, path, publicKey, new RecordingLog(), null, CancellationToken.None));

            Assert.Equal(UpdateStage.Verify, ex.Stage);
            Assert.Contains("公钥", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Signature_is_verified_against_the_raw_entry_not_the_normalized_one()
    {
        // 签名覆盖 feed 里写的原始条目（"1.2.4"）。规范化条目的规范化 JSON 是 "1.2.4.0"，签名必不匹配。
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        (ECDsa key, string publicKey) = NewKey();
        using (temp)
        using (key)
        {
            ReleaseEntry raw = Signed(release, key);
            ValidatedFeed validated = ReleaseFeedValidator.Validate(FeedFixtures.Feed(raw), new RecordingLog());
            ReleaseEntry normalized = Assert.Single(validated.Document.Releases);
            Assert.Equal(new Version(1, 2, 4, 0), normalized.Version);

            PackageVerifier.Verify(validated.RawOf(normalized), path, publicKey, new RecordingLog(), null, CancellationToken.None);
            Assert.Throws<UpdateFailedException>(() => PackageVerifier.Verify(normalized, path, publicKey, new RecordingLog(), null, CancellationToken.None));
        }
    }

    [Fact]
    public void Cancellation_is_propagated()
    {
        (TempDirectory temp, string path, ReleaseEntry release) = Stage();
        using (temp)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => PackageVerifier.Verify(release, path, null, new RecordingLog(), null, cts.Token));
        }
    }

    [Fact]
    public void ComputeSha256_matches_one_shot_hash()
    {
        (TempDirectory temp, string path, _) = Stage();
        using (temp)
        {
            Assert.Equal(FeedFixtures.Sha256Hex(Package), PackageVerifier.ComputeSha256(path, null, CancellationToken.None));
        }
    }
}
