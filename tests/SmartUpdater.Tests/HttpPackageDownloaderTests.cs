using System.Net;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class HttpPackageDownloaderTests
{
    private static readonly byte[] Body = CreateBody(200_000);

    private sealed class Collector : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    /// <summary>记录退避时长并立即放行（默认），或由测试控制放行。</summary>
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Func<TimeSpan, CancellationToken, Task> Callback => (delay, ct) => { Delays.Add(delay); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
    }

    private static byte[] CreateBody(int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    private static ReleaseEntry Release(LoopbackHttpServer server, string path, byte[] body, bool absolute = false)
        => FeedFixtures.Release("1.2.4", url: absolute ? new Uri(server.BaseUri, path).ToString() : path.TrimStart('/'), size: body.Length, sha256: FeedFixtures.Sha256Hex(body));

    private static (HttpPackageDownloader Downloader, RecordingDelay Delay, ManualTimeProvider Time) Create(LoopbackHttpServer server, Uri? baseUri = null)
    {
        var time = new ManualTimeProvider();
        var delay = new RecordingDelay();
        var downloader = new HttpPackageDownloader(new HttpClient(), baseUri ?? new Uri(server.BaseUri, "updates/releases.json"), time) { DelayAsync = delay.Callback };
        return (downloader, delay, time);
    }

    [Fact]
    public async Task Downloads_relative_url_to_destination_with_progress_and_no_part_left()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/App-1.2.4.zip", Body, etag: "\"p1\"");
        (HttpPackageDownloader downloader, _, _) = Create(server);
        var progress = new Collector();
        string destination = temp.Resolve("cache/1.2.4.0.zip");

        string result = await downloader.DownloadAsync(Release(server, "packages/App-1.2.4.zip", Body), destination, progress, CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.Equal(Body, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.etag"));
        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Null(request.Range);
        Assert.Equal("/updates/packages/App-1.2.4.zip", request.Uri.AbsolutePath);
        Assert.Equal(Body.Length, progress.Reports[^1].BytesReceived);
        Assert.Equal(100, progress.Reports[^1].Percent);
        Assert.True(progress.Reports.Count >= 2);
    }

    [Fact]
    public async Task Absolute_url_ignores_base_uri()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/elsewhere/p.zip", Body);
        (HttpPackageDownloader downloader, _, _) = Create(server, new Uri("https://other.example/updates/releases.json"));

        await downloader.DownloadAsync(Release(server, "/elsewhere/p.zip", Body, absolute: true), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal("/elsewhere/p.zip", Assert.Single(server.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Relative_url_without_base_uri_fails_before_any_request()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        var downloader = new HttpPackageDownloader(new HttpClient(), baseUri: null);

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(FeedFixtures.Release("1.0", url: "packages/x.zip"), temp.Resolve("x.zip"), null, CancellationToken.None));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public void ResolvePackageUri_handles_relative_absolute_and_rooted()
    {
        var feed = new Uri("https://server/updates/releases.json");

        Assert.Equal(new Uri("https://server/updates/packages/x.zip"), HttpPackageDownloader.ResolvePackageUri(feed, "packages/x.zip"));
        Assert.Equal(new Uri("https://cdn/x.zip"), HttpPackageDownloader.ResolvePackageUri(feed, "https://cdn/x.zip"));
        Assert.Equal(new Uri("https://server/pkgs/x.zip"), HttpPackageDownloader.ResolvePackageUri(feed, "/pkgs/x.zip"));
        Assert.Equal(new Uri("https://cdn/x.zip"), HttpPackageDownloader.ResolvePackageUri(null, "https://cdn/x.zip"));
        Assert.Throws<ArgumentException>(() => HttpPackageDownloader.ResolvePackageUri(null, "packages/x.zip"));
    }

    [Fact]
    public async Task Existing_complete_file_is_returned_without_a_request()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        temp.WriteBytes("1.2.4.0.zip", Body);
        (HttpPackageDownloader downloader, _, _) = Create(server);

        string result = await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("1.2.4.0.zip"), null, CancellationToken.None);

        Assert.Equal(temp.Resolve("1.2.4.0.zip"), result);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Resumes_from_part_file_with_range_and_if_range()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body, etag: "\"p1\"");
        temp.WriteBytes("p.zip.part", Body[..50_000]);
        temp.WriteFile("p.zip.part.etag", "\"p1\"");
        (HttpPackageDownloader downloader, _, _) = Create(server);
        var progress = new Collector();

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), progress, CancellationToken.None);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("bytes=50000-", request.Range);
        Assert.Equal("\"p1\"", request.IfRange);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
        Assert.True(progress.Reports[0].BytesReceived > 50_000);
        Assert.False(temp.Exists("p.zip.part.etag"));
    }

    [Fact]
    public async Task Stale_etag_makes_server_answer_200_and_download_restarts_from_zero()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body, etag: "\"p2\"");
        temp.WriteBytes("p.zip.part", CreateBody(50_000));     // 旧内容
        temp.WriteFile("p.zip.part.etag", "\"p1\"");
        (HttpPackageDownloader downloader, _, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal("bytes=50000-", Assert.Single(server.Requests).Range);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Server_without_range_support_answers_200_and_download_restarts_from_zero()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body, supportRange: false);
        temp.WriteBytes("p.zip.part", Body[..50_000]);
        (HttpPackageDownloader downloader, _, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Oversized_part_file_is_discarded_and_download_starts_from_zero()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body);
        temp.WriteBytes("p.zip.part", CreateBody(Body.Length + 10));
        (HttpPackageDownloader downloader, _, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Null(Assert.Single(server.Requests).Range);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Range_not_satisfiable_discards_the_part_file_and_restarts_from_zero()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        int first = 1;
        server.Map("/updates/packages/p.zip", async (request, response) =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                response.StatusCode = 416;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentLength64 = Body.Length;
            await response.OutputStream.WriteAsync(Body, TestContext.Current.CancellationToken);
            response.Close();
        });
        temp.WriteBytes("p.zip.part", Body[..50_000]);
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("bytes=50000-", server.Requests[0].Range);
        Assert.Null(server.Requests[1].Range);
        Assert.Empty(delay.Delays);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
        Assert.False(temp.Exists("p.zip.part"));
    }

    [Fact]
    public async Task Connection_abort_mid_body_backs_off_and_resumes()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.AbortAfter("/updates/packages/p.zip", Body, bytes: 70_000, etag: "\"p1\"");
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.Null(server.Requests[0].Range);
        Assert.NotNull(server.Requests[1].Range);
        Assert.StartsWith("bytes=", server.Requests[1].Range, StringComparison.Ordinal);
        Assert.Equal("\"p1\"", server.Requests[1].IfRange);
        Assert.Equal([TimeSpan.FromSeconds(30)], delay.Delays);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Too_many_requests_honours_retry_after_and_then_succeeds()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.FailThenServe("/updates/packages/p.zip", failures: 2, HttpStatusCode.TooManyRequests, Body, retryAfter: "7");
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], delay.Delays);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Service_unavailable_backs_off_exponentially_and_gives_up_after_five_retries()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.FailThenServe("/updates/packages/p.zip", failures: 99, HttpStatusCode.ServiceUnavailable, Body);
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(6, server.Requests.Count);
        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480)],
            delay.Delays);
    }

    [Fact]
    public async Task Not_found_fails_immediately_without_retry()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(Release(server, "packages/missing.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Single(server.Requests);
        Assert.Empty(delay.Delays);
        Assert.False(temp.Exists("p.zip.part"));
    }

    [Fact]
    public async Task Server_without_range_support_reports_progress_from_zero_after_restart()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body, supportRange: false);
        temp.WriteBytes("p.zip.part", Body[..50_000]);
        (HttpPackageDownloader downloader, _, _) = Create(server);
        var progress = new Collector();

        await downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), progress, CancellationToken.None);

        Assert.Equal(Body.Length, progress.Reports[^1].BytesReceived);
    }

    [Fact]
    public async Task Cancellation_keeps_the_part_file_for_a_later_resume()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.StallAfter("/updates/packages/p.zip", Body, bytes: 70_000);
        (HttpPackageDownloader downloader, _, _) = Create(server);
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new CancelOnProgress(cts, gate);

        Task<string> download = downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), progress, cts.Token);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(temp.Exists("p.zip.part"));
        Assert.True(new FileInfo(temp.Resolve("p.zip.part")).Length > 0);
        Assert.False(temp.Exists("p.zip"));
    }

    private sealed class CancelOnProgress(CancellationTokenSource cts, TaskCompletionSource gate) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            cts.Cancel();
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Idle_read_timeout_is_treated_as_a_transient_failure_and_resumes()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.StallAfter("/updates/packages/p.zip", Body, bytes: 70_000, etag: "\"p1\"");
        (HttpPackageDownloader downloader, RecordingDelay delay, ManualTimeProvider time) = Create(server);

        Task<string> download = downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("p.zip"), null, CancellationToken.None);
        await time.WaitForTimersAsync(1);                       // 读超时计时器已武装（读在挂起）

        // 真实网络：带超时的轮询，等到 .part 里确实有了数据再推进假时钟（不用固定睡眠赌到达时间）。
        string part = temp.Resolve("p.zip.part");
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(part) || new FileInfo(part).Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "10 s 内 .part 没有收到任何数据");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        time.Advance(downloader.ReadTimeout);

        await download.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(2, server.Requests.Count);
        Assert.NotNull(server.Requests[1].Range);
        Assert.Equal([TimeSpan.FromSeconds(30)], delay.Delays);
        Assert.Equal(Body, File.ReadAllBytes(temp.Resolve("p.zip")));
    }

    [Fact]
    public async Task Disk_failure_is_not_retried_and_leaves_no_part_file()
    {
        using var server = new LoopbackHttpServer();
        using var temp = new TempDirectory();
        server.ServeBytes("/updates/packages/p.zip", Body);
        temp.WriteFile("notadir", "x");                          // 目标目录位置被一个文件占着 → 建目录失败
        (HttpPackageDownloader downloader, RecordingDelay delay, _) = Create(server);

        await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadAsync(Release(server, "packages/p.zip", Body), temp.Resolve("notadir/p.zip"), null, CancellationToken.None));

        Assert.Empty(delay.Delays);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(0, null, 30)]
    [InlineData(1, null, 60)]
    [InlineData(2, null, 120)]
    [InlineData(3, null, 240)]
    [InlineData(4, null, 480)]
    [InlineData(5, null, 600)]
    [InlineData(10, null, 600)]
    [InlineData(0, 7, 7)]
    [InlineData(3, 7, 7)]
    [InlineData(0, 1200, 600)]
    [InlineData(0, -5, 0)]
    public void ComputeBackoff_is_exponential_capped_and_honours_retry_after(int retryIndex, int? retryAfterSeconds, int expectedSeconds)
    {
        TimeSpan? retryAfter = retryAfterSeconds is null ? null : TimeSpan.FromSeconds(retryAfterSeconds.Value);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HttpPackageDownloader.ComputeBackoff(retryIndex, retryAfter));
    }

    [Fact]
    public void Constructor_validates_arguments_and_exposes_base_uri()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpPackageDownloader(null!));
        var d = new HttpPackageDownloader(new HttpClient(), new Uri("https://server/updates/releases.json"));
        Assert.Equal(new Uri("https://server/updates/releases.json"), d.BaseUri);
        Assert.Null(new HttpPackageDownloader(new HttpClient()).BaseUri);
    }
}
