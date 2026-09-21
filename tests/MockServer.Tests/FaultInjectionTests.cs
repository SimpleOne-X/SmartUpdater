using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class FaultInjectionTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private const string Feed = """{"schemaVersion":1,"channel":"stable","releases":[]}""";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task ArrangeFeedAsync(CancellationToken cancellationToken)
    {
        await fixture.ResetAsync(cancellationToken);
        File.WriteAllText(Path.Combine(fixture.Root, "releases.json"), Feed);
    }

    [Fact]
    public async Task Injected_404_replaces_an_existing_file()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage armed = await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":404}"""),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, armed.StatusCode);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Injected_503_carries_retry_after()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503,"retryAfterSeconds":30}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter!.Delta);
    }

    [Fact]
    public async Task Injected_429_is_supported()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":429,"retryAfterSeconds":1}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Fail_count_lets_the_request_through_afterwards()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503,"failCount":2}"""),
            TestContext.Current.CancellationToken);

        HttpStatusCode[] observed =
        [
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode,
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode,
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode,
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode,
        ];

        Assert.Equal(
            [
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.OK,
                HttpStatusCode.OK,
            ],
            observed);
    }

    [Fact]
    public async Task Fault_without_fail_count_never_recovers()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response =
                await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task Fault_is_scoped_to_the_exact_path()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "a.zip"), [1, 2, 3]);

        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage feed =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);
        using HttpResponseMessage package =
            await fixture.Client.GetAsync("packages/a.zip", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, feed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, package.StatusCode);
    }

    [Fact]
    public async Task Control_plane_stays_reachable_while_a_fault_is_armed()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);

        // 一条打在控制面自己身上的故障 —— 如果控制面没有被豁免，下面的 reset 就再也发不出去。
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/_control/reset","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage reset =
            await fixture.Client.PostAsync("_control/reset", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task Reset_clears_faults()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_faults_clears_them_without_touching_reports()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""{"eventType":"Heartbeat"}"""),
            TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage deleted =
            await fixture.Client.DeleteAsync("_control/faults", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage feed =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);

        JsonNode reports = (await fixture.Client.GetFromJsonAsync<JsonNode>(
            "_control/reports", TestContext.Current.CancellationToken))!;
        Assert.Single(reports.AsArray());
    }

    [Fact]
    public async Task Reports_are_readable_and_clearable_through_the_control_plane()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""{"eventType":"Updated","toVersion":"1.2.4"}"""),
            TestContext.Current.CancellationToken);

        JsonNode reports = (await fixture.Client.GetFromJsonAsync<JsonNode>(
            "_control/reports", TestContext.Current.CancellationToken))!;
        Assert.Equal("1.2.4", reports.AsArray()[0]!["toVersion"]!.GetValue<string>());

        using HttpResponseMessage cleared =
            await fixture.Client.DeleteAsync("_control/reports", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        JsonNode after = (await fixture.Client.GetFromJsonAsync<JsonNode>(
            "_control/reports", TestContext.Current.CancellationToken))!;
        Assert.Empty(after.AsArray());
    }

    [Theory]
    [InlineData("""{"path":"/releases.json","status":200}""")]
    [InlineData("""{"path":"/releases.json","status":99}""")]
    [InlineData("""{"status":503}""")]
    [InlineData("""{"path":"/releases.json"}""")]
    [InlineData("not json at all")]
    public async Task Invalid_fault_requests_are_rejected(string body)
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.PostAsync("_control/faults", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Path_without_a_leading_slash_is_normalised()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);

        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- 边界：路径匹配忽略大小写与查询串、重新 POST 归零、响应体为空，
    // ---- 以及"字段类型不对也必须是 400 而不是 500"。

    [Fact]
    public async Task Fault_matching_ignores_case_and_query_string()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("RELEASES.JSON?cache-bust=1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Control_plane_exemption_ignores_case()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/_control/reset","status":503}"""),
            TestContext.Current.CancellationToken);

        // 路由不分大小写，所以换个大小写写法也要照样豁免，否则控制面还是能被锁死。
        using HttpResponseMessage reset =
            await fixture.Client.PostAsync("_CONTROL/Reset", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task Posting_the_same_path_again_restarts_the_fail_count()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        const string Arm = """{"path":"/releases.json","status":503,"failCount":1}""";

        await fixture.Client.PostAsync("_control/faults", Json(Arm), TestContext.Current.CancellationToken);
        HttpStatusCode first =
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode;
        HttpStatusCode second =
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode;

        await fixture.Client.PostAsync("_control/faults", Json(Arm), TestContext.Current.CancellationToken);
        HttpStatusCode afterRearm =
            (await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken)).StatusCode;

        Assert.Equal(
            [HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable],
            [first, second, afterRearm]);
    }

    [Fact]
    public async Task Fault_response_has_no_body()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"path":"/releases.json","status":503,"retryAfterSeconds":-1}""")]
    [InlineData("""{"path":"/releases.json","status":503,"retryAfterSeconds":"soon"}""")]
    [InlineData("""{"path":"/releases.json","status":503,"retryAfterSeconds":1.5}""")]
    [InlineData("""{"path":"/releases.json","status":503,"failCount":-1}""")]
    [InlineData("""{"path":"/releases.json","status":503,"failCount":"2"}""")]
    [InlineData("""{"path":"/releases.json","status":"503"}""")]
    [InlineData("""{"path":503,"status":503}""")]
    [InlineData("""{"path":null,"status":503}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Fault_requests_with_wrongly_typed_fields_are_rejected(string body)
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.PostAsync("_control/faults", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Fault_matching_is_exact_not_a_prefix()
    {
        await ArrangeFeedAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Root, "releases.json.bak"), "x");
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "a.zip"), [1, 2, 3]);

        // 两条故障各自是另一个真实文件的字面前缀：/releases.json 之于 /releases.json.bak，/packages 之于 /packages/a.zip。
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/releases.json","status":503}"""),
            TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/faults",
            Json("""{"path":"/packages","status":503}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage backup =
            await fixture.Client.GetAsync("releases.json.bak", TestContext.Current.CancellationToken);
        using HttpResponseMessage package =
            await fixture.Client.GetAsync("packages/a.zip", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, package.StatusCode);
    }
}
