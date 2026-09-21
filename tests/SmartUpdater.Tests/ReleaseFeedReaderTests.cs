using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseFeedReaderTests
{
    private const string Good = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": { "pollIntervalSeconds": 300, "jitterWindowSeconds": 600, "heartbeatIntervalSeconds": 21600 },
          "releases": [
            { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
              "minUpdatableFrom": "1.0.0", "mode": "mandatory", "rolloutPercent": 100, "notes": "修复若干问题",
              "signature": "base64-ecdsa-p256-over-canonical-release-json" }
          ]
        }
        """;

    private static ReleaseFeedDocument Parse(string json) => ReleaseFeedReader.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parses_the_spec_example_keeping_versions_as_written()
    {
        ReleaseFeedDocument doc = Parse(Good);

        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal("stable", doc.Channel);
        Assert.Equal(300, doc.Client!.PollIntervalSeconds);
        Assert.Equal(600, doc.Client.JitterWindowSeconds);
        Assert.Equal(21600, doc.Client.HeartbeatIntervalSeconds);
        ReleaseEntry r = Assert.Single(doc.Releases);
        Assert.Equal(new Version(1, 2, 4), r.Version);
        Assert.Equal(new Version(1, 0, 0), r.MinUpdatableFrom);
        Assert.Equal(UpdateMode.Mandatory, r.Mode);
        Assert.Equal(12345678, r.Package.Size);
        Assert.Equal("修复若干问题", r.Notes);
        Assert.Equal("base64-ecdsa-p256-over-canonical-release-json", r.Signature);
        Assert.Empty(doc.ParseWarnings);
    }

    [Fact]
    public void Bad_entries_are_skipped_individually_and_reported_as_warnings()
    {
        // releases[6] 是守卫："version": null 必须在读取处被 RespectNullableAnnotations 挡下
        const string json = """
            { "schemaVersion": 1, "releases": [
                { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "a.zip", "size": 10, "sha256": "a" } },
                { "version": "not-a-version", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "b.zip", "size": 10, "sha256": "b" } },
                { "version": "1.2.2", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "c.zip", "size": 10 } },
                { "version": "1.2.1", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "d.zip", "size": 10, "sha256": "d" }, "mode": 7 },
                "not an object",
                null,
                { "version": null, "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "f.zip", "size": 10, "sha256": "f" } },
                { "version": "1.2.0", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "e.zip", "size": 10, "sha256": "e" } }
            ] }
            """;

        ReleaseFeedDocument doc = Parse(json);

        Assert.Equal([new Version(1, 2, 4), new Version(1, 2, 0)], doc.Releases.Select(r => r.Version));
        Assert.Equal(6, doc.ParseWarnings.Count);
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[1]:", StringComparison.Ordinal) && w.Contains("not-a-version", StringComparison.Ordinal));
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[2]:", StringComparison.Ordinal) && w.Contains("sha256", StringComparison.Ordinal));
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[3]:", StringComparison.Ordinal));
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[4]:", StringComparison.Ordinal));
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[5]:", StringComparison.Ordinal));
        Assert.Contains(doc.ParseWarnings, w => w.StartsWith("releases[6]:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "releases": [] }""")]
    [InlineData("""{ "schemaVersion": "1", "releases": [] }""")]
    [InlineData("""{ "schemaVersion": 1.5, "releases": [] }""")]
    [InlineData("""{ "schemaVersion": null, "releases": [] }""")]
    public void Missing_or_non_integer_schema_version_reads_as_zero(string json)
    {
        Assert.Equal(0, Parse(json).SchemaVersion);
    }

    [Fact]
    public void Missing_releases_yields_empty_list_without_warning()
    {
        ReleaseFeedDocument doc = Parse("""{ "schemaVersion": 1 }""");

        Assert.Empty(doc.Releases);
        Assert.Empty(doc.ParseWarnings);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "releases": null }""")]
    [InlineData("""{ "schemaVersion": 1, "releases": {} }""")]
    [InlineData("""{ "schemaVersion": 1, "releases": "x" }""")]
    public void Releases_that_is_not_an_array_yields_empty_list_with_a_warning(string json)
    {
        ReleaseFeedDocument doc = Parse(json);

        Assert.Empty(doc.Releases);
        string w = Assert.Single(doc.ParseWarnings);
        Assert.Contains("releases", w, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "client": "fast" }""")]
    [InlineData("""{ "schemaVersion": 1, "client": { "pollIntervalSeconds": "300" } }""")]
    public void Unparseable_client_section_falls_back_to_null_with_a_warning(string json)
    {
        ReleaseFeedDocument doc = Parse(json);

        Assert.Null(doc.Client);
        string w = Assert.Single(doc.ParseWarnings);
        Assert.Contains("client", w, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_client_section_is_silently_null()
    {
        ReleaseFeedDocument doc = Parse("""{ "schemaVersion": 1, "client": null }""");

        Assert.Null(doc.Client);
        Assert.Empty(doc.ParseWarnings);
    }

    [Fact]
    public void Utf8_bom_is_stripped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Good)];

        ReleaseFeedDocument doc = ReleaseFeedReader.Parse(bytes);

        Assert.Equal(1, doc.SchemaVersion);
        Assert.Single(doc.Releases);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    public void Malformed_or_non_object_documents_throw_JsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => Parse(json));
    }

    [Fact]
    public void Channel_of_wrong_type_is_ignored_with_a_warning()
    {
        ReleaseFeedDocument doc = Parse("""{ "schemaVersion": 1, "channel": 5 }""");

        Assert.Null(doc.Channel);
        Assert.Single(doc.ParseWarnings);
    }
}
