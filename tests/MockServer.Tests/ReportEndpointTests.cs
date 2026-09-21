using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class ReportEndpointTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Report_is_accepted_with_202_and_an_empty_body()
    {
        fixture.Host.State.ClearReports();

        using HttpResponseMessage response = await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""{"eventType":"Updated","fromVersion":"1.2.3","toVersion":"1.2.4"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Report_is_stored_and_readable_back()
    {
        fixture.Host.State.ClearReports();

        await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""{"eventType":"Failed","stage":"Download","errorMessage":"boom"}"""),
            TestContext.Current.CancellationToken);

        IReadOnlyList<JsonNode> stored = fixture.Host.State.SnapshotReports();

        JsonNode report = Assert.Single(stored);
        Assert.Equal("Failed", report["eventType"]!.GetValue<string>());
        Assert.Equal("Download", report["stage"]!.GetValue<string>());
        Assert.Equal("boom", report["errorMessage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reports_are_kept_in_arrival_order()
    {
        fixture.Host.State.ClearReports();

        foreach (string version in (string[])["1.0.0", "1.1.0", "1.2.0"])
        {
            await fixture.Client.PostAsync(
                "api/v1/update-reports",
                Json($$"""{"eventType":"Heartbeat","toVersion":"{{version}}"}"""),
                TestContext.Current.CancellationToken);
        }

        string[] versions =
        [
            .. fixture.Host.State.SnapshotReports().Select(r => r["toVersion"]!.GetValue<string>()),
        ];

        Assert.Equal(["1.0.0", "1.1.0", "1.2.0"], versions);
    }

    [Fact]
    public async Task Unknown_fields_are_preserved_verbatim()
    {
        fixture.Host.State.ClearReports();

        await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""{"eventType":"Updated","someFutureField":{"nested":42}}"""),
            TestContext.Current.CancellationToken);

        JsonNode report = Assert.Single(fixture.Host.State.SnapshotReports());
        Assert.Equal(42, report["someFutureField"]!["nested"]!.GetValue<int>());
    }

    [Fact]
    public async Task Malformed_json_is_rejected_and_not_stored()
    {
        fixture.Host.State.ClearReports();

        using HttpResponseMessage response = await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("{ this is not json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Host.State.SnapshotReports());
    }

    [Fact]
    public async Task A_json_array_is_rejected_because_a_report_must_be_an_object()
    {
        fixture.Host.State.ClearReports();

        using HttpResponseMessage response = await fixture.Client.PostAsync(
            "api/v1/update-reports",
            Json("""[{"eventType":"Updated"}]"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Host.State.SnapshotReports());
    }

    [Fact]
    public async Task Get_on_the_report_endpoint_is_not_allowed()
    {
        using HttpResponseMessage response =
            await fixture.Client.GetAsync("api/v1/update-reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // 边界形状：MockServer 要"什么形状都原样收"，就不能因为 body 的形状怪而 500 或误入队。

    [Theory]
    [InlineData("", "an empty body")]
    [InlineData("null", "the JSON literal null")]
    [InlineData("\"Updated\"", "a bare string")]
    [InlineData("42", "a bare number")]
    [InlineData("true", "a bare boolean")]
    public async Task Bodies_that_are_not_a_json_object_are_rejected_and_not_stored(string body, string because)
    {
        fixture.Host.State.ClearReports();

        using HttpResponseMessage response = await fixture.Client.PostAsync(
            "api/v1/update-reports", Json(body), TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{because} should be 400, was {(int)response.StatusCode}");
        Assert.Empty(fixture.Host.State.SnapshotReports());
    }

    [Fact]
    public async Task A_report_with_a_non_string_event_type_is_still_accepted_and_stored()
    {
        fixture.Host.State.ClearReports();

        using HttpResponseMessage response = await fixture.Client.PostAsync(
            "api/v1/update-reports", Json("""{"eventType":3,"toVersion":"1.0.0"}"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonNode report = Assert.Single(fixture.Host.State.SnapshotReports());
        Assert.Equal(3, report["eventType"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"eventType":"Updated","toVersion":"1.2.4"}""", """REPORT Updated {"eventType":"Updated","toVersion":"1.2.4"}""")]
    [InlineData("""{"toVersion":"1.2.4"}""", """REPORT <none> {"toVersion":"1.2.4"}""")]
    [InlineData("""{"eventType":null}""", """REPORT <none> {"eventType":null}""")]
    [InlineData("""{"eventType":3}""", """REPORT 3 {"eventType":3}""")]
    [InlineData("""{"eventType":{"a":1}}""", """REPORT {"a":1} {"eventType":{"a":1}}""")]
    public void The_console_line_is_REPORT_then_event_type_then_the_json(string body, string expected)
    {
        var report = (JsonObject)JsonNode.Parse(body)!;

        Assert.Equal(expected, ReportEndpoint.FormatLine(report));
    }

    [Fact]
    public void The_console_line_stays_on_one_line_even_when_the_event_type_has_a_line_break()
    {
        var report = (JsonObject)JsonNode.Parse("""{"eventType":"a\nb","note":"x\ny"}""")!;

        string line = ReportEndpoint.FormatLine(report);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("REPORT ", line, StringComparison.Ordinal);
    }
}
