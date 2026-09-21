using System.Net;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class HttpReleaseFeedTests
{
    private const string FeedUrl = "https://server/updates/releases.json";

    private const string FeedJson = """
        { "schemaVersion": 1, "client": { "pollIntervalSeconds": 120 }, "releases": [
            { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "packages/App-1.2.4.zip", "size": 10, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" }, "mode": "mandatory" }
        ] }
        """;

    private static (HttpReleaseFeed Feed, StubHttpMessageHandler Handler) Create()
    {
        var handler = new StubHttpMessageHandler();
        return (new HttpReleaseFeed(handler.CreateClient(), FeedUrl), handler);
    }

    [Fact]
    public async Task First_request_has_no_conditional_header_and_returns_document_with_etag()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, FeedJson, etag: "\"v1\"");

        FeedResult result = await feed.GetAsync(null, CancellationToken.None);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri(FeedUrl), request.Uri);
        Assert.Null(request.IfNoneMatch);
        Assert.False(result.IsNotModified);
        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal(new Version(1, 2, 4), Assert.Single(result.Document!.Releases).Version);   // feed 层保持原始写法，规范化在 Validate
        Assert.Equal(120, result.Document.Client!.PollIntervalSeconds);
    }

    [Fact]
    public async Task Known_etag_is_sent_back_and_304_yields_NotModified()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.NotModified);

        FeedResult result = await feed.GetAsync("\"v1\"", CancellationToken.None);

        Assert.Equal("\"v1\"", Assert.Single(handler.Requests).IfNoneMatch);
        Assert.Same(FeedResult.NotModified, result);
    }

    [Fact]
    public async Task Weak_etag_round_trips_verbatim()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, FeedJson, etag: "W/\"abc\"");

        FeedResult first = await feed.GetAsync(null, CancellationToken.None);
        handler.Enqueue(HttpStatusCode.NotModified);
        await feed.GetAsync(first.ETag, CancellationToken.None);

        Assert.Equal("W/\"abc\"", first.ETag);
        Assert.Equal("W/\"abc\"", handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task Missing_etag_header_yields_null_etag()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, FeedJson);

        FeedResult result = await feed.GetAsync(null, CancellationToken.None);

        Assert.Null(result.ETag);
        Assert.NotNull(result.Document);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Non_success_status_throws_HttpRequestException_with_status(HttpStatusCode status)
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(status, "ignored");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetAsync(null, CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(FeedUrl, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task Malformed_body_throws_JsonException(string body)
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, body);

        await Assert.ThrowsAnyAsync<JsonException>(() => feed.GetAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_schema_version_passes_through_the_feed_and_is_rejected_by_the_validator()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{ "schemaVersion": 2, "releases": [] }""");

        FeedResult result = await feed.GetAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Document!.SchemaVersion);
        Assert.Throws<FeedRejectedException>(() => ReleaseFeedValidator.Validate(result.Document, new RecordingLog()));
    }

    [Fact]
    public async Task Invalid_version_string_only_drops_that_entry()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """
            { "schemaVersion": 1, "releases": [
                { "version": "oops", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "a.zip", "size": 10, "sha256": "a" } },
                { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "b.zip", "size": 10, "sha256": "b" } }
            ] }
            """);

        FeedResult result = await feed.GetAsync(null, CancellationToken.None);

        Assert.Equal(new Version(1, 2, 4), Assert.Single(result.Document!.Releases).Version);
        Assert.Single(result.Document.ParseWarnings);
    }

    [Fact]
    public async Task Connection_failure_and_timeout_propagate_unchanged()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.EnqueueException(new HttpRequestException("refused", null, null));
        handler.EnqueueException(new TaskCanceledException("timeout", new TimeoutException()));

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetAsync(null, CancellationToken.None));
        var timeout = await Assert.ThrowsAsync<TaskCanceledException>(() => feed.GetAsync(null, CancellationToken.None));
        Assert.IsType<TimeoutException>(timeout.InnerException);
    }

    [Fact]
    public async Task Cancelled_token_throws_OperationCanceledException()
    {
        (HttpReleaseFeed feed, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, FeedJson);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.GetAsync(null, cts.Token));
    }

    [Theory]
    [InlineData("releases.json")]
    [InlineData("/updates/releases.json")]
    [InlineData("ftp://server/releases.json")]
    [InlineData("")]
    public void Constructor_rejects_non_absolute_or_non_http_urls(string url)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HttpReleaseFeed(new HttpClient(), url));
    }

    [Fact]
    public void Constructor_rejects_null_client_and_exposes_uri()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpReleaseFeed(null!, FeedUrl));
        Assert.Equal(new Uri(FeedUrl), new HttpReleaseFeed(new HttpClient(), FeedUrl).FeedUri);
        Assert.Equal(new Uri("http://localhost:8080/r.json"), new HttpReleaseFeed(new HttpClient(), new Uri("http://localhost:8080/r.json")).FeedUri);
    }
}
