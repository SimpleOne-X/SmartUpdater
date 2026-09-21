using System.Net;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class HttpUpdateReporterTests
{
    private const string ReportUrl = "https://server/api/v1/update-reports";

    private static UpdateReport Report() => new()
    {
        EventType = UpdateEventType.Failed,
        DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        MachineName = "PC-01",
        Stage = UpdateStage.Download,
        FromVersion = new Version(1, 2, 3, 0),
        ToVersion = new Version(1, 2, 4, 0),
        IsSuccess = false,
        ErrorMessage = "boom",
        ReportedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
    };

    private static (HttpUpdateReporter Reporter, StubHttpMessageHandler Handler) Create()
    {
        var handler = new StubHttpMessageHandler();
        return (new HttpUpdateReporter(handler.CreateClient(), ReportUrl), handler);
    }

    [Fact]
    public async Task Posts_json_and_returns_true_on_202()
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        string? contentType = null;
        handler.Enqueue(request =>
        {
            contentType = request.Content!.Headers.ContentType!.ToString();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });

        bool ok = await reporter.SendAsync(Report(), CancellationToken.None);

        Assert.True(ok);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(ReportUrl), request.Uri);
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Contains("\"eventType\":\"Failed\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"Download\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"deviceGuid\":\"11111111-2222-3333-4444-555555555555\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"toVersion\":\"1.2.4.0\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"errorMessage\":\"boom\"", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Created, true)]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Found, false)]
    public async Task Only_2xx_counts_as_delivered(HttpStatusCode status, bool expected)
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(status, body: "{\"ignored\":true}");

        Assert.Equal(expected, await reporter.SendAsync(Report(), CancellationToken.None));
    }

    [Fact]
    public async Task Transport_failures_return_false_instead_of_throwing()
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        handler.EnqueueException(new HttpRequestException("refused"));
        handler.EnqueueException(new TaskCanceledException("timeout", new TimeoutException()));
        handler.EnqueueException(new IOException("reset"));

        Assert.False(await reporter.SendAsync(Report(), CancellationToken.None));
        Assert.False(await reporter.SendAsync(Report(), CancellationToken.None));
        Assert.False(await reporter.SendAsync(Report(), CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.Accepted);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter.SendAsync(Report(), cts.Token));
    }

    [Fact]
    public async Task Cancellation_during_send_propagates_instead_of_returning_false()
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        using var cts = new CancellationTokenSource();
        handler.Enqueue(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("canceled", null, cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter.SendAsync(Report(), cts.Token));
    }

    [Fact]
    public async Task Heartbeat_report_is_serialized_without_null_fields()
    {
        (HttpUpdateReporter reporter, StubHttpMessageHandler handler) = Create();
        handler.Enqueue(HttpStatusCode.Accepted);
        var heartbeat = new UpdateReport
        {
            EventType = UpdateEventType.Heartbeat,
            DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            IsSuccess = true,
            ReportedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        };

        await reporter.SendAsync(heartbeat, CancellationToken.None);

        string body = handler.Requests[0].Body!;
        Assert.Contains("\"eventType\":\"Heartbeat\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stage\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"logTail\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api/v1/update-reports")]
    [InlineData("ftp://server/x")]
    [InlineData("")]
    public void Constructor_rejects_non_absolute_or_non_http_urls(string url)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HttpUpdateReporter(new HttpClient(), url));
    }

    [Fact]
    public void Constructor_rejects_null_client_and_report_and_exposes_uri()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpUpdateReporter(null!, ReportUrl));
        Assert.Equal(new Uri(ReportUrl), new HttpUpdateReporter(new HttpClient(), ReportUrl).ReportUri);
    }

    [Fact]
    public async Task Null_report_throws()
    {
        (HttpUpdateReporter reporter, _) = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => reporter.SendAsync(null!, CancellationToken.None));
    }
}
