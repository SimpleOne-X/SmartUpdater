using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ProtocolSerializationTests
{
    [Fact]
    public void Feed_round_trips_with_all_fields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": { "pollIntervalSeconds": 300, "jitterWindowSeconds": 600 },
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678, "sha256": "abc" },
              "minUpdatableFrom": "1.0.0",
              "mode": "mandatory",
              "rolloutPercent": 25,
              "notes": "修复若干问题"
            }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;

        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal("stable", doc.Channel);
        Assert.Equal(300, doc.Client!.PollIntervalSeconds);
        var r = Assert.Single(doc.Releases);
        Assert.Equal(new Version(1, 2, 4), r.Version);
        Assert.Equal(new Version(1, 0, 0), r.MinUpdatableFrom);
        Assert.Equal(UpdateMode.Mandatory, r.Mode);
        Assert.Equal(25, r.RolloutPercent);
        Assert.Equal(12345678, r.Package.Size);
    }

    [Fact]
    public void Missing_optional_fields_fall_back_to_defaults()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "releases": [
            {
              "version": "2.0",
              "releasedAt": "2026-01-01T00:00:00Z",
              "package": { "url": "p.zip", "size": 1, "sha256": "x" }
            }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;
        var r = Assert.Single(doc.Releases);

        Assert.Null(doc.Client);
        Assert.Null(r.MinUpdatableFrom);
        Assert.Equal(UpdateMode.Optional, r.Mode);        // 缺省是 optional
        Assert.Equal(100, r.RolloutPercent);              // 缺省是全量
    }

    [Fact]
    public void Manifest_file_policy_defaults_to_replace()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "version": "1.2.4",
          "files": [
            { "path": "MyApp.exe", "sha256": "a", "size": 10 },
            { "path": "appsettings.json", "sha256": "b", "size": 20, "policy": "preserve" }
          ]
        }
        """;

        var m = JsonSerializer.Deserialize<PackageManifest>(json, SmartUpdaterJson.Options)!;

        Assert.Equal(new Version(1, 2, 4), m.Version);
        Assert.Equal(FilePolicy.Replace, m.Files[0].Policy);
        Assert.Equal(FilePolicy.Preserve, m.Files[1].Policy);
    }

    [Fact]
    public void Serializing_writes_camel_case_and_string_enums()
    {
        var m = new PackageManifest
        {
            SchemaVersion = 1,
            Version = new Version(1, 0, 0),
            Files = [new ManifestFile { Path = "a.txt", Sha256 = "h", Size = 1, Policy = FilePolicy.Preserve }],
        };

        string json = JsonSerializer.Serialize(m, SmartUpdaterJson.Options);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"policy\":\"preserve\"", json);
        Assert.Contains("\"version\":\"1.0.0\"", json);
    }

    [Fact]
    public void Invalid_version_string_throws_JsonException_not_FormatException()
    {
        const string json = """
        { "schemaVersion": 1, "version": "not-a-version", "files": [] }
        """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<PackageManifest>(json, SmartUpdaterJson.Options));
    }

    // Releases 必须是 set：源生成器对 init 属性会丢弃初始值，JSON 缺字段时得到 null。此测试守住"缺省为空集合"。
    [Fact]
    public void Feed_without_releases_defaults_to_empty_collection()
    {
        const string json = """
        { "schemaVersion": 1 }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;

        Assert.NotNull(doc.Releases);
        Assert.Empty(doc.Releases);
    }

    // Files 必须是 set：原因同上。此测试守住"缺省为空集合"。
    [Fact]
    public void Manifest_without_files_defaults_to_empty_collection()
    {
        const string json = """
        { "schemaVersion": 1, "version": "1.2.4" }
        """;

        var m = JsonSerializer.Deserialize<PackageManifest>(json, SmartUpdaterJson.Options)!;

        Assert.NotNull(m.Files);
        Assert.Empty(m.Files);
    }

    // package.sha256 必填，缺失即拒绝安装。守的是 PackageInfo.Sha256 上的 required。
    [Fact]
    public void Release_package_without_sha256_throws_JsonException()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678 }
            }
          ]
        }
        """;

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options));

        Assert.Contains("sha256", ex.Message);            // 确认是因为缺 sha256 才抛，而不是别的原因
    }

    // 守写出路径：Mode 必须写成小写 camelCase 字符串（不是 PascalCase，也不是数字），Version / MinUpdatableFrom 必须写成字符串。
    [Fact]
    public void Serializing_feed_writes_camel_case_mode_and_string_versions()
    {
        var doc = new ReleaseFeedDocument
        {
            SchemaVersion = 1,
            Releases =
            [
                new ReleaseEntry
                {
                    Version = new Version(1, 2, 4),
                    ReleasedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
                    Package = new PackageInfo { Url = "packages/MyApp-1.2.4.zip", Size = 12345678, Sha256 = "abc" },
                    MinUpdatableFrom = new Version(1, 0, 0),
                    Mode = UpdateMode.Mandatory,
                },
            ],
        };

        string json = JsonSerializer.Serialize(doc, SmartUpdaterJson.Options);

        Assert.Contains("\"mode\":\"mandatory\"", json);
        Assert.Contains("\"version\":\"1.2.4\"", json);
        Assert.Contains("\"minUpdatableFrom\":\"1.0.0\"", json);
    }

    // 守写出路径：FilePolicy 的两个取值都必须写成小写 camelCase。preserve 另有 Serializing_writes_camel_case_and_string_enums 覆盖，这里覆盖 replace。
    [Fact]
    public void Serializing_manifest_writes_camel_case_policy_for_both_values()
    {
        var m = new PackageManifest
        {
            SchemaVersion = 1,
            Version = new Version(1, 2, 4),
            Files =
            [
                new ManifestFile { Path = "MyApp.exe", Sha256 = "a", Size = 10, Policy = FilePolicy.Replace },
                new ManifestFile { Path = "appsettings.json", Sha256 = "b", Size = 20, Policy = FilePolicy.Preserve },
            ],
        };

        string json = JsonSerializer.Serialize(m, SmartUpdaterJson.Options);

        Assert.Contains("\"policy\":\"replace\"", json);
        Assert.Contains("\"policy\":\"preserve\"", json);
    }

    // 守 NullableVersionJsonConverter 的非法值分支：必须抛 JsonException（且落在 minUpdatableFrom 字段上），不能漏出 FormatException。
    [Fact]
    public void Invalid_min_updatable_from_throws_JsonException_not_FormatException()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "p.zip", "size": 1, "sha256": "x" },
              "minUpdatableFrom": "not-a-version"
            }
          ]
        }
        """;

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options));

        Assert.Equal("$.releases[0].minUpdatableFrom", ex.Path);
    }

    // 协议里的 feed 示例逐字段读取。示例里被省略的 sha256 串（"..."）补全为空输入的 SHA-256，其前 32 位与示例一致。
    [Fact]
    public void Spec_feed_example_reads_every_field()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": {
            "pollIntervalSeconds": 300,
            "jitterWindowSeconds": 600,
            "heartbeatIntervalSeconds": 21600
          },
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": {
                "url": "packages/MyApp-1.2.4.zip",
                "size": 12345678,
                "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
              },
              "minUpdatableFrom": "1.0.0",
              "mode": "mandatory",
              "rolloutPercent": 100,
              "notes": "修复若干问题",
              "signature": "base64-ecdsa-p256-over-canonical-release-json"
            }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;

        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal("stable", doc.Channel);
        Assert.NotNull(doc.Client);
        Assert.Equal(300, doc.Client.PollIntervalSeconds);
        Assert.Equal(600, doc.Client.JitterWindowSeconds);
        Assert.Equal(21600, doc.Client.HeartbeatIntervalSeconds);

        var r = Assert.Single(doc.Releases);
        Assert.Equal(new Version(1, 2, 4), r.Version);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero), r.ReleasedAt);
        Assert.Equal("packages/MyApp-1.2.4.zip", r.Package.Url);
        Assert.Equal(12345678, r.Package.Size);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", r.Package.Sha256);
        Assert.Equal(new Version(1, 0, 0), r.MinUpdatableFrom);
        Assert.Equal(UpdateMode.Mandatory, r.Mode);
        Assert.Equal(100, r.RolloutPercent);
        Assert.Equal("修复若干问题", r.Notes);
        Assert.Equal("base64-ecdsa-p256-over-canonical-release-json", r.Signature);
    }

    // 协议里的 manifest 示例逐字段读取。示例里被省略的 sha256 串（"..."）各补一个互不相同的合法 SHA-256，以便发现字段错位。
    [Fact]
    public void Spec_manifest_example_reads_every_field()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "version": "1.2.4",
          "files": [
            { "path": "MyApp.exe",        "sha256": "cd476e7e35bbf12adc927573e9f6b04f0fc5cb646bfb6b5681b857c7d7a239ba", "size": 8123456 },
            { "path": "appsettings.json", "sha256": "d3ceffcff98e755128623fcbf6c4e23787f1a198ca383ccc0aabbb3c94ac4036", "size": 512, "policy": "preserve" },
            { "path": "Resources/logo.png", "sha256": "5dc7619b75402f40d9274e2159d6727304253fa914b0ce26204c26ff471f0d1a", "size": 20480 }
          ]
        }
        """;

        var m = JsonSerializer.Deserialize<PackageManifest>(json, SmartUpdaterJson.Options)!;

        Assert.Equal(1, m.SchemaVersion);
        Assert.Equal(new Version(1, 2, 4), m.Version);
        Assert.Equal(3, m.Files.Count);

        Assert.Equal("MyApp.exe", m.Files[0].Path);
        Assert.Equal("cd476e7e35bbf12adc927573e9f6b04f0fc5cb646bfb6b5681b857c7d7a239ba", m.Files[0].Sha256);
        Assert.Equal(8123456, m.Files[0].Size);
        Assert.Equal(FilePolicy.Replace, m.Files[0].Policy);

        Assert.Equal("appsettings.json", m.Files[1].Path);
        Assert.Equal("d3ceffcff98e755128623fcbf6c4e23787f1a198ca383ccc0aabbb3c94ac4036", m.Files[1].Sha256);
        Assert.Equal(512, m.Files[1].Size);
        Assert.Equal(FilePolicy.Preserve, m.Files[1].Policy);

        Assert.Equal("Resources/logo.png", m.Files[2].Path);
        Assert.Equal("5dc7619b75402f40d9274e2159d6727304253fa914b0ce26204c26ff471f0d1a", m.Files[2].Sha256);
        Assert.Equal(20480, m.Files[2].Size);
        Assert.Equal(FilePolicy.Replace, m.Files[2].Policy);
    }

    // 前向兼容：服务端给 feed / manifest 新增字段，旧客户端必须照常解析。
    // 守的是"未知成员被忽略"这个默认行为：往 [JsonSourceGenerationOptions] 里加 UnmappedMemberHandling = Disallow，
    // 或给某个类型写一个不会跳过未知子树的手写转换器，这条测试都会红。
    // 每一层对象都插入了未知成员——feed：根、client、releases[i]、package；manifest：根、files[i]——
    // 值的形态覆盖字符串 / 数字 / 布尔 / 嵌套对象 / 数组，位置有的在已知成员之前、有的在中间、有的在最后。
    // 基线样本分别取自 Feed_round_trips_with_all_fields 与 Manifest_file_policy_defaults_to_replace，
    // 它们是现有测试里含全部所需层级的最小样本；带 future 前缀的成员之外，两份 JSON 完全相同。
    [Fact]
    public void Unknown_members_in_feed_and_manifest_are_ignored()
    {
        const string feed = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": { "pollIntervalSeconds": 300, "jitterWindowSeconds": 600 },
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678, "sha256": "abc" },
              "minUpdatableFrom": "1.0.0",
              "mode": "mandatory",
              "rolloutPercent": 25,
              "notes": "修复若干问题"
            }
          ]
        }
        """;

        const string feedWithUnknowns = """
        {
          "futureRoot": { "experiment": { "enabled": true, "buckets": [1, 2, 3] }, "owner": null },
          "schemaVersion": 1,
          "channel": "stable",
          "client": { "pollIntervalSeconds": 300, "futureClient": [60, "two", [3], { "four": 4 }], "jitterWindowSeconds": 600 },
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678, "sha256": "abc", "futurePackage": 42.5 },
              "minUpdatableFrom": "1.0.0",
              "mode": "mandatory",
              "rolloutPercent": 25,
              "notes": "修复若干问题",
              "futureRelease": "some field that does not exist yet"
            }
          ]
        }
        """;

        const string manifest = """
        {
          "schemaVersion": 1,
          "version": "1.2.4",
          "files": [
            { "path": "MyApp.exe", "sha256": "a", "size": 10 },
            { "path": "appsettings.json", "sha256": "b", "size": 20, "policy": "preserve" }
          ]
        }
        """;

        const string manifestWithUnknowns = """
        {
          "schemaVersion": 1,
          "version": "1.2.4",
          "futureManifest": true,
          "files": [
            { "futureFile": { "tags": ["a", "b"] }, "path": "MyApp.exe", "sha256": "a", "size": 10 },
            { "path": "appsettings.json", "sha256": "b", "size": 20, "policy": "preserve", "futureFile": "x" }
          ]
        }
        """;

        // 不抛异常，且所有已知字段的值与不带未知成员时完全相同：
        // 两边各自写回 JSON 后必须逐字一致（写出会带出模型上的每一个已知属性）。
        var expectedFeed = JsonSerializer.Deserialize<ReleaseFeedDocument>(feed, SmartUpdaterJson.Options)!;
        var actualFeed = JsonSerializer.Deserialize<ReleaseFeedDocument>(feedWithUnknowns, SmartUpdaterJson.Options)!;
        var expectedManifest = JsonSerializer.Deserialize<PackageManifest>(manifest, SmartUpdaterJson.Options)!;
        var actualManifest = JsonSerializer.Deserialize<PackageManifest>(manifestWithUnknowns, SmartUpdaterJson.Options)!;

        Assert.Equal(
            JsonSerializer.Serialize(expectedFeed, SmartUpdaterJson.Options),
            JsonSerializer.Serialize(actualFeed, SmartUpdaterJson.Options));
        Assert.Equal(
            JsonSerializer.Serialize(expectedManifest, SmartUpdaterJson.Options),
            JsonSerializer.Serialize(actualManifest, SmartUpdaterJson.Options));

        // 再对带未知成员的结果抽查每一层的已知字段（未知成员两侧各取一个），免得两边解析出同一份残缺结果时空转。
        Assert.Equal(1, actualFeed.SchemaVersion);
        Assert.Equal("stable", actualFeed.Channel);
        Assert.Equal(300, actualFeed.Client!.PollIntervalSeconds);
        Assert.Equal(600, actualFeed.Client.JitterWindowSeconds);
        var release = Assert.Single(actualFeed.Releases);
        Assert.Equal(new Version(1, 2, 4), release.Version);
        Assert.Equal(UpdateMode.Mandatory, release.Mode);
        Assert.Equal("修复若干问题", release.Notes);
        Assert.Equal("packages/MyApp-1.2.4.zip", release.Package.Url);
        Assert.Equal("abc", release.Package.Sha256);

        Assert.Equal(1, actualManifest.SchemaVersion);
        Assert.Equal(new Version(1, 2, 4), actualManifest.Version);
        Assert.Equal(2, actualManifest.Files.Count);
        Assert.Equal("MyApp.exe", actualManifest.Files[0].Path);
        Assert.Equal(10, actualManifest.Files[0].Size);
        Assert.Equal("appsettings.json", actualManifest.Files[1].Path);
        Assert.Equal(FilePolicy.Preserve, actualManifest.Files[1].Policy);
    }

    // 版本串保持原样：两段 / 三段 / 四段的版本串读进来再写出，必须与输入逐字相同——转换器既不补零也不截断。
    // feed 条目里的 version 走 VersionJsonConverter、minUpdatableFrom 走 NullableVersionJsonConverter，两条写出路径都要守。
    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.4")]
    [InlineData("1.2.4.0")]
    [InlineData("1.2.4.5")]
    public void Version_strings_are_written_back_exactly_as_they_were_read(string version)
    {
        string json = $$"""
        {
          "schemaVersion": 1,
          "releases": [
            {
              "version": "{{version}}",
              "releasedAt": "2026-01-01T00:00:00Z",
              "package": { "url": "p.zip", "size": 1, "sha256": "x" },
              "minUpdatableFrom": "{{version}}"
            }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;
        string written = JsonSerializer.Serialize(doc, SmartUpdaterJson.Options);

        using JsonDocument output = JsonDocument.Parse(written);
        JsonElement release = output.RootElement.GetProperty("releases")[0];
        Assert.Equal(version, release.GetProperty("version").GetString());
        Assert.Equal(version, release.GetProperty("minUpdatableFrom").GetString());
    }

    // 读进来的 1.2.4 与 1.2.4.0 是两个不相等的 Version（Revision 未指定时为 -1，不等于 0）。
    // 归一化只在客户端决策边界做；这里钉住转换器不归一化，防止有人无意中在转换器里悄悄归一化。
    [Fact]
    public void Three_segment_and_four_segment_versions_are_not_equal_after_reading()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-01-01T00:00:00Z",
              "package": { "url": "p.zip", "size": 1, "sha256": "x" },
              "minUpdatableFrom": "1.2.4"
            },
            {
              "version": "1.2.4.0",
              "releasedAt": "2026-01-01T00:00:00Z",
              "package": { "url": "p.zip", "size": 1, "sha256": "x" },
              "minUpdatableFrom": "1.2.4.0"
            }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ReleaseFeedDocument>(json, SmartUpdaterJson.Options)!;

        Assert.NotEqual(doc.Releases[0].Version, doc.Releases[1].Version);
        Assert.NotEqual(doc.Releases[0].MinUpdatableFrom, doc.Releases[1].MinUpdatableFrom);
    }
}
