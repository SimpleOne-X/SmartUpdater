using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class StaticHostingTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private const string Feed = """{"schemaVersion":1,"channel":"stable","releases":[]}""";

    private static byte[] MakePackage(int size)
    {
        byte[] bytes = new byte[size];
        new Random(20260919).NextBytes(bytes);
        return bytes;
    }

    private async Task<string> GetTextAsync(string relativeUrl)
    {
        using HttpResponseMessage response =
            await fixture.Client.GetAsync(relativeUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Feed_is_served_with_a_strong_etag_and_accept_ranges()
    {
        File.WriteAllText(Path.Combine(fixture.Root, "releases.json"), Feed);

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("releases.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Feed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task Matching_if_none_match_returns_304_with_an_empty_body()
    {
        File.WriteAllText(Path.Combine(fixture.Root, "etag-304.json"), Feed);

        using HttpResponseMessage first =
            await fixture.Client.GetAsync("etag-304.json", TestContext.Current.CancellationToken);
        EntityTagHeaderValue etag = first.Headers.ETag!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "etag-304.json");
        request.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(etag, second.Headers.ETag);
    }

    [Fact]
    public async Task Stale_if_none_match_returns_200_with_the_body()
    {
        File.WriteAllText(Path.Combine(fixture.Root, "etag-stale.json"), Feed);

        using var request = new HttpRequestMessage(HttpMethod.Get, "etag-stale.json");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"deadbeef\""));
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Feed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Etag_changes_when_the_file_changes()
    {
        string path = Path.Combine(fixture.Root, "etag-change.json");
        File.WriteAllText(path, Feed);
        using HttpResponseMessage before =
            await fixture.Client.GetAsync("etag-change.json", TestContext.Current.CancellationToken);

        File.WriteAllText(path, """{"schemaVersion":1,"channel":"beta","releases":[]}""");
        using HttpResponseMessage after =
            await fixture.Client.GetAsync("etag-change.json", TestContext.Current.CancellationToken);

        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
    }

    [Fact]
    public async Task Closed_range_returns_206_with_exactly_those_bytes()
    {
        byte[] package = MakePackage(64 * 1024);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "closed.zip"), package);

        using var request = new HttpRequestMessage(HttpMethod.Get, "packages/closed.zip");
        request.Headers.Range = new RangeHeaderValue(1024, 2047);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        byte[] slice = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1024, slice.Length);
        Assert.Equal(package[1024..2048], slice);
        Assert.NotNull(response.Content.Headers.ContentRange);
        Assert.Equal(package.Length, response.Content.Headers.ContentRange!.Length);
    }

    [Fact]
    public async Task Open_ended_range_returns_the_tail()
    {
        byte[] package = MakePackage(64 * 1024);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "open.zip"), package);

        using var request = new HttpRequestMessage(HttpMethod.Get, "packages/open.zip");
        request.Headers.Range = new RangeHeaderValue(package.Length - 100, null);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(package[^100..], await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unsatisfiable_range_returns_416()
    {
        byte[] package = MakePackage(1024);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "short.zip"), package);

        using var request = new HttpRequestMessage(HttpMethod.Get, "packages/short.zip");
        request.Headers.Range = new RangeHeaderValue(99_999_999, null);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task If_range_honours_the_etag()
    {
        byte[] package = MakePackage(8192);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "ifrange.zip"), package);

        using HttpResponseMessage probe =
            await fixture.Client.GetAsync("packages/ifrange.zip", TestContext.Current.CancellationToken);
        EntityTagHeaderValue etag = probe.Headers.ETag!;

        using var fresh = new HttpRequestMessage(HttpMethod.Get, "packages/ifrange.zip");
        fresh.Headers.Range = new RangeHeaderValue(0, 15);
        fresh.Headers.IfRange = new RangeConditionHeaderValue(etag);
        using HttpResponseMessage partial = await fixture.Client.SendAsync(fresh, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Get, "packages/ifrange.zip");
        stale.Headers.Range = new RangeHeaderValue(0, 15);
        stale.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"deadbeef\""));
        using HttpResponseMessage full = await fixture.Client.SendAsync(stale, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(package.Length, (await full.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Missing_file_returns_404()
    {
        using HttpResponseMessage response =
            await fixture.Client.GetAsync("packages/nope.zip", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_extension_is_still_served()
    {
        File.WriteAllBytes(Path.Combine(fixture.Root, "blob.smartupdater"), Encoding.UTF8.GetBytes("payload"));

        using HttpResponseMessage response =
            await fixture.Client.GetAsync("blob.smartupdater", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Overlay_content_is_served_by_the_middleware_with_its_own_etag_and_the_disk_is_untouched()
    {
        string path = Path.Combine(fixture.Root, "overlay-set.json");
        File.WriteAllText(path, Feed);
        using HttpResponseMessage onDisk =
            await fixture.Client.GetAsync("overlay-set.json", TestContext.Current.CancellationToken);

        const string Overlaid = """{"schemaVersion":1,"channel":"beta","releases":[]}""";
        try
        {
            // 故意不带前导 "/"：Normalize 要把它补成静态文件中间件传进来的形式。
            fixture.Host.Files.Set(
                "overlay-set.json", Encoding.UTF8.GetBytes(Overlaid), new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));

            using HttpResponseMessage overlaid =
                await fixture.Client.GetAsync("overlay-set.json", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, overlaid.StatusCode);
            Assert.Equal(Overlaid, await overlaid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(overlaid.Headers.ETag);
            Assert.NotEqual(onDisk.Headers.ETag, overlaid.Headers.ETag);
            Assert.Equal(Feed, File.ReadAllText(path));
        }
        finally
        {
            fixture.Host.Files.Remove("overlay-set.json");
        }
    }

    [Fact]
    public async Task Removing_or_clearing_the_overlay_falls_back_to_the_disk_file()
    {
        File.WriteAllText(Path.Combine(fixture.Root, "overlay-a.json"), Feed);
        File.WriteAllText(Path.Combine(fixture.Root, "overlay-b.json"), Feed);
        var stamp = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        byte[] overlaid = Encoding.UTF8.GetBytes("overlaid");
        fixture.Host.Files.Set("/overlay-a.json", overlaid, stamp);
        fixture.Host.Files.Set("/overlay-b.json", overlaid, stamp);
        Assert.Equal("overlaid", await GetTextAsync("overlay-a.json"));
        Assert.Equal("overlaid", await GetTextAsync("overlay-b.json"));

        Assert.True(fixture.Host.Files.Remove("/overlay-a.json"));
        Assert.False(fixture.Host.Files.Remove("/overlay-a.json"));
        Assert.Equal(Feed, await GetTextAsync("overlay-a.json"));
        Assert.Equal("overlaid", await GetTextAsync("overlay-b.json"));

        fixture.Host.Files.Clear();
        Assert.Equal(Feed, await GetTextAsync("overlay-b.json"));
    }

    [Fact]
    public async Task Relative_root_is_resolved_against_the_current_directory()
    {
        // MockServerOptions.TryParse 只检查目录存在；PhysicalFileProvider 却只认绝对路径。
        // 相对的 --root 必须被规范化，而不是让进程在构造时崩溃。
        string name = "sumock-rel-" + Guid.NewGuid().ToString("N");
        string absolute = Path.Combine(Environment.CurrentDirectory, name);
        Directory.CreateDirectory(absolute);
        try
        {
            File.WriteAllText(Path.Combine(absolute, "releases.json"), Feed);

            await using var host = new MockServerHost(new MockServerOptions(name, 0));
            await host.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };

            using HttpResponseMessage response =
                await client.GetAsync("releases.json", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Feed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(absolute, recursive: true);
        }
    }
}
