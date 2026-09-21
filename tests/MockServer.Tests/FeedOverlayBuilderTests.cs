using System.Text;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>直接测 <see cref="FeedOverlayBuilder"/>：纯函数，不起服务器，每个分支各占一条。</summary>
public sealed class FeedOverlayBuilderTests
{
    private const string DiskFeed = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": { "pollIntervalSeconds": 300, "heartbeatIntervalSeconds": 3600 },
          "releases": [
            { "version": "1.2.4", "package": { "url": "a.zip", "sha256": "aa" }, "mode": "optional", "rolloutPercent": 100 },
            { "version": "1.2.3", "package": { "url": "b.zip", "sha256": "bb" }, "mode": "optional", "rolloutPercent": 100 }
          ]
        }
        """;

    private static FeedOverlayRequest Request(
        string? body = null,
        string? version = null,
        int? rolloutPercent = null,
        string? mode = null,
        string? packageSha256 = null,
        int? schemaVersion = null,
        ClientPolicyPatch? client = null)
        => new(body, version, rolloutPercent, mode, packageSha256, schemaVersion, client);

    private static JsonNode BuildOk(string? disk, FeedOverlayRequest request)
    {
        Assert.True(FeedOverlayBuilder.TryBuild(disk, request, out byte[] bytes, out string? error), error);
        Assert.Null(error);
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
    }

    private static string BuildFails(string? disk, FeedOverlayRequest request)
    {
        Assert.False(FeedOverlayBuilder.TryBuild(disk, request, out byte[] bytes, out string? error));
        Assert.Empty(bytes);
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error;
    }

    [Theory]
    [InlineData("version")]
    [InlineData("rolloutPercent")]
    [InlineData("mode")]
    [InlineData("packageSha256")]
    [InlineData("schemaVersion")]
    [InlineData("client")]
    public void Body_together_with_any_single_patch_field_is_rejected(string field)
    {
        FeedOverlayRequest request = field switch
        {
            "version" => Request(body: "{}", version: "1.2.4"),
            "rolloutPercent" => Request(body: "{}", rolloutPercent: 5),
            "mode" => Request(body: "{}", mode: "mandatory"),
            "packageSha256" => Request(body: "{}", packageSha256: "x"),
            "schemaVersion" => Request(body: "{}", schemaVersion: 2),
            "client" => Request(body: "{}", client: new ClientPolicyPatch(1, null, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

        Assert.Contains("互斥", BuildFails(DiskFeed, request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("你好 ✓")]
    public void Body_is_emitted_verbatim_as_utf8_and_needs_no_disk_feed(string body)
    {
        Assert.True(FeedOverlayBuilder.TryBuild(null, Request(body: body), out byte[] bytes, out string? error));

        Assert.Null(error);
        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(body), bytes);
    }

    [Fact]
    public void Patch_without_a_disk_feed_is_rejected()
    {
        Assert.Contains("releases.json", BuildFails(null, Request(rolloutPercent: 5)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":2}""")]
    [InlineData("""{"releases":[{"version":"1.2.4","version":"1.2.3"}]}""")]
    public void Disk_feed_that_is_not_a_json_object_is_rejected_even_for_a_top_level_patch(string disk)
    {
        BuildFails(disk, Request(schemaVersion: 99));
    }

    [Fact]
    public void Request_with_no_fields_at_all_is_rejected()
    {
        Assert.Contains("没有要修改", BuildFails(DiskFeed, Request()), StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_an_entry_without_saying_what_to_change_is_rejected()
    {
        Assert.Contains("没有要修改", BuildFails(DiskFeed, Request(version: "1.2.4")), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_named_fields_change_and_everything_else_is_kept()
    {
        JsonNode result = BuildOk(
            DiskFeed,
            Request(
                version: "1.2.4",
                rolloutPercent: 5,
                mode: "mandatory",
                packageSha256: "deadbeef",
                schemaVersion: 2,
                client: new ClientPolicyPatch(PollIntervalSeconds: 1, JitterWindowSeconds: 0, HeartbeatIntervalSeconds: null)));

        JsonNode expected = JsonNode.Parse("""
            {
              "schemaVersion": 2,
              "channel": "stable",
              "client": { "pollIntervalSeconds": 1, "heartbeatIntervalSeconds": 3600, "jitterWindowSeconds": 0 },
              "releases": [
                { "version": "1.2.4", "package": { "url": "a.zip", "sha256": "deadbeef" }, "mode": "mandatory", "rolloutPercent": 5 },
                { "version": "1.2.3", "package": { "url": "b.zip", "sha256": "bb" }, "mode": "optional", "rolloutPercent": 100 }
              ]
            }
            """)!;
        Assert.True(JsonNode.DeepEquals(expected, result), result.ToJsonString());
    }

    [Theory]
    [InlineData("""{"releases":[]}""")]
    [InlineData("""{"releases":[],"client":null}""")]
    public void Client_segment_is_created_when_it_is_absent_or_null(string disk)
    {
        JsonNode result = BuildOk(disk, Request(client: new ClientPolicyPatch(2, 3, 4)));

        JsonNode client = result["client"]!;
        Assert.Equal(2, client["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Equal(3, client["jitterWindowSeconds"]!.GetValue<int>());
        Assert.Equal(4, client["heartbeatIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void Client_segment_that_is_not_an_object_is_rejected()
    {
        Assert.Contains(
            "client",
            BuildFails("""{"client":5,"releases":[]}""", Request(client: new ClientPolicyPatch(1, null, null))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Top_level_patch_does_not_need_releases()
    {
        JsonNode result = BuildOk("""{"schemaVersion":1}""", Request(schemaVersion: 99));

        Assert.Equal(99, result["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"releases":5}""")]
    [InlineData("""{"releases":{}}""")]
    public void Entry_patch_needs_a_releases_array(string disk)
    {
        Assert.Contains("releases", BuildFails(disk, Request(rolloutPercent: 5)), StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_patch_against_an_empty_releases_array_is_rejected()
    {
        Assert.Contains("为空", BuildFails("""{"releases":[]}""", Request(mode: "mandatory")), StringComparison.Ordinal);
    }

    [Fact]
    public void Releases_containing_a_non_object_are_rejected()
    {
        BuildFails("""{"releases":[5]}""", Request(mode: "mandatory"));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.4 ")]
    [InlineData("v1.2.4")]
    [InlineData("")]
    public void Version_is_matched_exactly(string version)
    {
        Assert.Contains("version", BuildFails(DiskFeed, Request(version: version, rolloutPercent: 5)), StringComparison.Ordinal);
    }

    [Fact]
    public void Version_is_validated_even_when_only_top_level_fields_change()
    {
        BuildFails(DiskFeed, Request(version: "9.9.9", schemaVersion: 2));
    }

    [Fact]
    public void Every_entry_with_the_selected_version_is_patched()
    {
        JsonNode result = BuildOk(
            """{"releases":[{"version":"1.0.0","mode":"optional"},{"version":"1.0.0","mode":"optional"},{"version":"0.9.0","mode":"optional"}]}""",
            Request(version: "1.0.0", mode: "mandatory"));

        JsonArray releases = result["releases"]!.AsArray();
        Assert.Equal("mandatory", releases[0]!["mode"]!.GetValue<string>());
        Assert.Equal("mandatory", releases[1]!["mode"]!.GetValue<string>());
        Assert.Equal("optional", releases[2]!["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"releases":[{"version":"1.2.4"}]}""")]
    [InlineData("""{"releases":[{"version":"1.2.4","package":5}]}""")]
    public void Sha256_patch_needs_a_package_object_on_the_selected_entry(string disk)
    {
        Assert.Contains("package", BuildFails(disk, Request(packageSha256: "x")), StringComparison.Ordinal);
    }

    [Fact]
    public void Sha256_patch_ignores_a_missing_package_on_entries_that_are_not_selected()
    {
        JsonNode result = BuildOk(
            """{"releases":[{"version":"1.2.4","package":{"sha256":"aa"}},{"version":"1.2.3"}]}""",
            Request(version: "1.2.4", packageSha256: "x"));

        Assert.Equal("x", result["releases"]![0]!["package"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Output_is_indented_with_two_spaces_and_keeps_non_ascii_and_html_characters_unescaped()
    {
        Assert.True(FeedOverlayBuilder.TryBuild(
            """{"schemaVersion":1,"notes":"修复 <b>&"}""",
            Request(schemaVersion: 2),
            out byte[] bytes,
            out _));

        string text = Encoding.UTF8.GetString(bytes);
        string[] lines = text.Split('\n');

        Assert.Equal("  \"schemaVersion\": 2,", lines[1].TrimEnd('\r'));
        Assert.Contains("\"notes\": \"修复 <b>&\"", text, StringComparison.Ordinal);
        Assert.NotEqual(0xEF, bytes[0]);
    }
}
