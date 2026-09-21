using System.Security.Cryptography;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal static class FeedFixtures
{
    public const string ValidSha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static readonly DateTimeOffset ReleasedAt = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static ReleaseEntry Release(
        string version,
        UpdateMode mode = UpdateMode.Mandatory,
        string? minUpdatableFrom = null,
        int rolloutPercent = 100,
        string? url = null,
        long size = 1000,
        string sha256 = ValidSha,
        string? notes = null,
        string? signature = null)
        => new()
        {
            Version = Version.Parse(version),
            ReleasedAt = ReleasedAt,
            Package = new PackageInfo { Url = url ?? $"packages/App-{version}.zip", Size = size, Sha256 = sha256 },
            MinUpdatableFrom = minUpdatableFrom is null ? null : Version.Parse(minUpdatableFrom),
            Mode = mode,
            RolloutPercent = rolloutPercent,
            Notes = notes,
            Signature = signature,
        };

    public static ReleaseFeedDocument Feed(params ReleaseEntry[] releases)
        => new() { SchemaVersion = 1, Channel = "stable", Releases = releases };

    public static ReleaseFeedDocument Feed(ClientPolicy? client, params ReleaseEntry[] releases)
        => new() { SchemaVersion = 1, Channel = "stable", Client = client, Releases = releases };
}
