using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// 端点层面对 <see cref="FeedOverlayTests"/> 的补充：那边守住"行为对不对"，这里守住
/// 它们拦不住的几处（同长度改值的 304、逐字下发不带 BOM、畸形底稿只改顶层字段），
/// 以及"失败必须说清原因、且不破坏已有叠加"这条契约。
/// </summary>
public sealed class FeedOverlayEndpointTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private const string DiskFeed = """
        {
          "schemaVersion": 1,
          "client": { "pollIntervalSeconds": 300 },
          "releases": [
            { "version": "1.2.4", "package": { "sha256": "aa" }, "mode": "optional", "rolloutPercent": 100 },
            { "version": "1.2.3", "package": { "sha256": "bb" }, "mode": "optional", "rolloutPercent": 100 }
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

    private async Task<HttpResponseMessage> OverlayAsync(string body, CancellationToken cancellationToken)
        => await fixture.Client.PostAsync("_control/feed-overlay", Json(body), cancellationToken);

    private async Task<JsonNode> GetFeedAsync(CancellationToken cancellationToken)
        => JsonNode.Parse(await fixture.Client.GetStringAsync("releases.json", cancellationToken))!;

    [Fact]
    public async Task Raw_body_is_served_byte_for_byte_without_a_bom()
    {
        // HttpContent.ReadAsStringAsync 会悄悄剥掉 BOM，所以逐字比较必须在字节层面做。
        await ArrangeAsync(TestContext.Current.CancellationToken);
        const string text = "{ 不是 json";

        using HttpResponseMessage applied = await OverlayAsync(
            $$"""{"body":"{{text}}"}""", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);
        byte[] served = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), served);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Empty_raw_body_is_served_as_an_empty_200()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        await OverlayAsync("""{"body":""}""", TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Changing_the_rollout_to_a_same_length_value_is_not_answered_with_304()
    {
        // 最真实的"陈旧 304"：灰度从 5 调到 7，内容长度一字不差，只有 LastModified 能让 ETag 变。
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);
        using HttpResponseMessage first =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":7}""", TestContext.Current.CancellationToken);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "releases.json");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using HttpResponseMessage second =
            await fixture.Client.SendAsync(conditional, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        JsonNode feed = JsonNode.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(7, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_second_patch_starts_from_the_disk_feed_not_from_the_first_overlay()
    {
        // 补丁不累加：想同时改两样，就在一次请求里都给。
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        await OverlayAsync("""{"version":"1.2.4","mode":"mandatory"}""", TestContext.Current.CancellationToken);

        JsonNode entry = (await GetFeedAsync(TestContext.Current.CancellationToken))["releases"]![0]!;
        Assert.Equal("mandatory", entry["mode"]!.GetValue<string>());
        Assert.Equal(100, entry["rolloutPercent"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"schemaVersion":99}""")]
    [InlineData("""{"client":{"pollIntervalSeconds":1}}""")]
    public async Task Top_level_patch_against_a_malformed_disk_feed_is_rejected(string request)
    {
        // 只改顶层字段时根本不会去找 releases，畸形底稿只能在解析那一步被拦下。
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(FeedPath, "{ broken");

        using HttpResponseMessage response = await OverlayAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_nulls_mean_not_given()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await OverlayAsync(
            """{"body":null,"version":null,"mode":null,"rolloutPercent":5,"client":null}""",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
        Assert.Equal(5, feed["releases"]![1]!["rolloutPercent"]!.GetValue<int>());
        Assert.Equal("optional", feed["releases"]![0]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Out_of_range_values_are_passed_through_on_purpose()
    {
        // 服务器的用途之一就是喂客户端"结构合法、取值离谱"的 feed，看它怎么自保，所以不做范围检查。
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await OverlayAsync(
            """{"version":"1.2.4","rolloutPercent":150,"client":{"pollIntervalSeconds":-1}}""",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(150, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
        Assert.Equal(-1, feed["client"]!["pollIntervalSeconds"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"version":"9.9.9","rolloutPercent":5}""", "9.9.9")]
    [InlineData("""{"rolloutpercent":5}""", "rolloutpercent")]
    [InlineData("""{"rolloutPercent":"5"}""", "rolloutPercent")]
    [InlineData("""{"mode":5}""", "mode")]
    [InlineData("""{"schemaVersion":1.5}""", "schemaVersion")]
    [InlineData("""{"client":5}""", "client")]
    [InlineData("""{"client":{"pollInterval":5}}""", "pollInterval")]
    [InlineData("""{"client":{"pollIntervalSeconds":"1"}}""", "pollIntervalSeconds")]
    [InlineData("""{"body":"{}","mode":"mandatory"}""", "互斥")]
    [InlineData("""{"body":5}""", "body")]
    [InlineData("""{}""", "没有要修改")]
    [InlineData("""{"version":"1.2.4"}""", "没有要修改")]
    [InlineData("""[]""", "JSON 对象")]
    [InlineData("""not json""", "JSON")]
    [InlineData("""{"mode":"optional","mode":"mandatory"}""", "重复")]
    [InlineData("""{"client":{"pollIntervalSeconds":1,"pollIntervalSeconds":2}}""", "重复")]
    public async Task Rejections_come_with_a_plain_text_reason(string request, string expectedInReason)
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await OverlayAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains(expectedInReason, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rejected_request_leaves_the_previous_overlay_in_place()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        await OverlayAsync("""{"version":"1.2.4","rolloutPercent":5}""", TestContext.Current.CancellationToken);

        using HttpResponseMessage rejected =
            await OverlayAsync("""{"version":"9.9.9","rolloutPercent":50}""", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        JsonNode feed = await GetFeedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, feed["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Delete_without_an_overlay_is_a_no_op()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.DeleteAsync("_control/feed-overlay", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(100, (await GetFeedAsync(TestContext.Current.CancellationToken))["releases"]![0]!["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task Raw_body_does_not_need_a_disk_feed()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        if (File.Exists(FeedPath))
        {
            File.Delete(FeedPath);
        }

        using HttpResponseMessage applied =
            await OverlayAsync("""{"body":"{\"schemaVersion\":1}"}""", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);

        Assert.Equal(
            """{"schemaVersion":1}""",
            await fixture.Client.GetStringAsync("releases.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overlay_has_no_effect_on_other_files()
    {
        await ArrangeAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Root, "other.json"), "{\"x\":1}");

        await OverlayAsync("""{"body":"overlaid"}""", TestContext.Current.CancellationToken);

        Assert.Equal("{\"x\":1}", await fixture.Client.GetStringAsync("other.json", TestContext.Current.CancellationToken));
    }
}
