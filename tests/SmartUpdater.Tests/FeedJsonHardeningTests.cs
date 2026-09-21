using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>转换器与源生成选项层面的加固：null 穿透、整数枚举、类型注册；并钉住"转换器不规范化版本"（验签依赖原始写法）。</summary>
public class FeedJsonHardeningTests
{
    private const string Entry = """
        { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z",
          "package": { "url": "p.zip", "size": 10, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" } }
        """;

    [Fact]
    public void ReleaseEntry_and_ClientPolicy_are_registered_in_the_source_generated_context()
    {
        ReleaseEntry entry = JsonSerializer.Deserialize(Entry, SmartUpdaterJsonContext.Default.ReleaseEntry)!;
        ClientPolicy policy = JsonSerializer.Deserialize("""{ "pollIntervalSeconds": 120 }""", SmartUpdaterJsonContext.Default.ClientPolicy)!;

        Assert.Equal("p.zip", entry.Package.Url);
        Assert.Equal(120, policy.PollIntervalSeconds);
    }

    [Fact]
    public void Converter_keeps_the_written_shape_of_versions()
    {
        // 签名覆盖的是 feed 里写的原始条目（规范化 JSON 写 Version.ToString()），
        // 所以转换器不得改写版本的段数；规范化只在客户端判定边界做。
        ReleaseEntry entry = JsonSerializer.Deserialize(
            """{ "version": "1.2", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" }, "minUpdatableFrom": "1.0.0" }""",
            SmartUpdaterJsonContext.Default.ReleaseEntry)!;

        Assert.Equal(new Version(1, 2), entry.Version);
        Assert.Equal(new Version(1, 0, 0), entry.MinUpdatableFrom);
        Assert.Equal("1.2", entry.Version.ToString());
    }

    [Theory]
    [InlineData("""{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" }, "mode": 1 }""")]
    [InlineData("""{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" }, "mode": 7 }""")]
    [InlineData("""{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" }, "mode": "bogus" }""")]
    public void Integer_or_unknown_enum_values_are_rejected(string json)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.ReleaseEntry));

        Assert.Equal("$.mode", ex.Path);
    }

    [Fact]
    public void Integer_file_policy_is_rejected_too()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            """{ "schemaVersion": 1, "version": "1.0", "files": [ { "path": "a", "sha256": "b", "size": 1, "policy": 0 } ] }""",
            SmartUpdaterJsonContext.Default.PackageManifest));
    }

    [Theory]
    [InlineData("""{ "version": null, "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" } }""", "$.version")]
    [InlineData("""{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": null, "size": 10, "sha256": "x" } }""", "$.package.url")]
    [InlineData("""{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": null } }""", "$.package.sha256")]
    public void Null_in_non_nullable_members_is_rejected(string json, string expectedPath)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.ReleaseEntry));

        Assert.Equal(expectedPath, ex.Path);
    }

    [Fact]
    public void Null_releases_collection_is_rejected_by_whole_document_deserialization()
    {
        // 与 ReleaseFeedReader 的"releases 为 null → 空列表 + 警告"并不矛盾：那是 Parse 手工解析的另一条路径。
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{ "schemaVersion": 1, "releases": null }""", SmartUpdaterJsonContext.Default.ReleaseFeedDocument));
    }

    [Fact]
    public void Null_manifest_files_collection_is_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{ "schemaVersion": 1, "version": "1.0", "files": null }""", SmartUpdaterJsonContext.Default.PackageManifest));
    }

    [Fact]
    public void Nullable_members_still_accept_null()
    {
        ReleaseEntry entry = JsonSerializer.Deserialize(
            """{ "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 10, "sha256": "x" }, "notes": null, "minUpdatableFrom": null, "signature": null }""",
            SmartUpdaterJsonContext.Default.ReleaseEntry)!;
        ReleaseFeedDocument doc = JsonSerializer.Deserialize("""{ "schemaVersion": 1, "client": null, "channel": null }""", SmartUpdaterJsonContext.Default.ReleaseFeedDocument)!;

        Assert.Null(entry.Notes);
        Assert.Null(entry.MinUpdatableFrom);
        Assert.Null(doc.Client);
        Assert.Null(doc.Channel);
    }
}
