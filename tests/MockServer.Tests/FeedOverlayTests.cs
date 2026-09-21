using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class FeedOverlayTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private const string DiskFeed = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "client": {
            "pollIntervalSeconds": 300
          },
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.4.zip", "size": 100, "sha256": "aa" },
              "mode": "optional",
              "rolloutPercent": 100
            },
            {
              "version": "1.2.3",
              "releasedAt": "2026-09-17T10:00:00Z",
              "package": { "url": "packages/MyApp-1.2.3.zip", "size": 90, "sha256": "bb" },
              "mode": "optional",
              "rolloutPercent": 100
            }
          ]
        }
        """;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private string FeedPath => Path.Combine(fixture.Root, "releases.json");

    private async Task ArrangeAsync(CancellationToken cancellationToken)
    {
        await fixture.ResetAsync(cancellationToken);
        File.WriteAllText(FeedPath, DiskFeed);
    }

    private Task<HttpResponseMessage> OverlayAsync(string body, CancellationToken cancellationToken)
        => fixture.Client.PostAsync("_control/feed-overlay", Json(body), cancellationToken);

    private async Task<JsonNode> GetFeedAsync(CancellationToken cancellationToken)
    {
        string text = await fixture.Client.GetStringAsync("releases.json", cancellationToken);
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Without_an_overlay_the_disk_feed_is_served()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Rollout_percent_can_be_changed_at_runtime()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage applied = await OverlayAsync(
            """{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
        Assert.Equal(100, feed["releases"]![1]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Mode_can_be_changed_at_runtime()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"version":"1.2.4","mode":"mandatory"}""", TestContext.Current.CancellationToken);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal("mandatory", feed["releases"]![0]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_segment_can_be_changed_at_runtime()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync(
            """{"client":{"pollIntervalSeconds":1,"jitterWindowSeconds":0,"heartbeatIntervalSeconds":600}}""",
            TestContext.Current.CancellationToken);

        JsonNode client = (await GetFeedAsync(TestContext.Current.CancellationToken))["client"]!;
        Assert.Equal(1, client["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Equal(0, client["jitterWindowSeconds"]!.GetValue<int>());
        Assert.Equal(600, client["heartbeatIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Package_sha256_can_be_corrupted_on_purpose()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync(
            """{"version":"1.2.4","packageSha256":"deadbeef"}""", TestContext.Current.CancellationToken);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal("deadbeef", feed["releases"]![0]!["package"]!["sha256"]!.GetValue<string>());
        Assert.Equal("bb", feed["releases"]![1]!["package"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_schema_version_can_be_served()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"schemaVersion":99}""", TestContext.Current.CancellationToken);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(99, feed["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Raw_body_serves_malformed_json_with_200()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"body":"{ this is not json"}""", TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{ this is not json",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overlay_never_touches_the_file_on_disk()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        string before = File.ReadAllText(FeedPath);

        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);
        await fixture.Client.GetStringAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllText(FeedPath));
    }

    [Fact]
    public async Task Overlay_changes_the_etag_so_clients_do_not_get_a_stale_304()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage before =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);
        EntityTagHeaderValue original = before.Headers.ETag!;

        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        // 客户端拿着旧 ETag 回来问：必须得到 200 + 新内容，而不是 304。
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "releases.json");
        conditional.Headers.IfNoneMatch.Add(original);
        using HttpResponseMessage after =
            await fixture.Client.SendAsync(conditional, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(original, after.Headers.ETag);
    }

    [Fact]
    public async Task Two_overlays_of_the_same_length_still_change_the_etag()
    {
        // 这条专门守住：ETag = LastModified ^ Length，长度不变时只有时间戳能救。
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"body":"AAAAAAAAAA"}""", TestContext.Current.CancellationToken);
        using HttpResponseMessage first =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        await OverlayAsync("""{"body":"BBBBBBBBBB"}""", TestContext.Current.CancellationToken);
        using HttpResponseMessage second =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(
            "BBBBBBBBBB",
            await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
    }

    [Fact]
    public async Task Overlaid_feed_still_supports_conditional_requests()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        using HttpResponseMessage first =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "releases.json");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using HttpResponseMessage second =
            await fixture.Client.SendAsync(conditional, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_overlay()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        using HttpResponseMessage removed =
            await fixture.Client.DeleteAsync("_control/feed-overlay", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reset_removes_the_overlay()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(FeedPath, DiskFeed);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Null_version_applies_the_patch_to_every_release()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"mode":"mandatory"}""", TestContext.Current.CancellationToken);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal("mandatory", feed["releases"]![0]!["mode"]!.GetValue<string>());
        Assert.Equal("mandatory", feed["releases"]![1]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_version_is_rejected()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await OverlayAsync("""{"version":"9.9.9","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_and_patch_fields_together_are_rejected()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await OverlayAsync("""{"body":"{}","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_against_a_missing_disk_feed_is_rejected()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        if (File.Exists(FeedPath))
        {
            File.Delete(FeedPath);
        }

        using HttpResponseMessage response =
            await OverlayAsync("""{"rolloutPercent":5}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_against_a_malformed_disk_feed_is_rejected()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(FeedPath, "{ broken");

        using HttpResponseMessage response =
            await OverlayAsync("""{"rolloutPercent":5}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
