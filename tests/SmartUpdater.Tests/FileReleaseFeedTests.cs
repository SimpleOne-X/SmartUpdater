using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class FileReleaseFeedTests
{
    private const string FeedJson = """
        { "schemaVersion": 1, "releases": [
            { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "packages/App-1.2.4.zip", "size": 10, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" } }
        ] }
        """;

    [Fact]
    public async Task Reads_feed_and_returns_content_hash_as_etag()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("updates/releases.json", FeedJson);
        var feed = new FileReleaseFeed(temp.Resolve("updates/releases.json"));

        FeedResult result = await feed.GetAsync(null, CancellationToken.None);

        Assert.False(result.IsNotModified);
        Assert.Equal(new Version(1, 2, 4), Assert.Single(result.Document!.Releases).Version);
        Assert.Equal(FileReleaseFeed.ETagPrefix + FeedFixtures.Sha256Hex(Encoding.UTF8.GetBytes(FeedJson)), result.ETag);
        Assert.Equal(temp.Resolve("updates"), feed.FeedDirectory);
        Assert.True(feed.FeedUri.IsFile);
    }

    [Fact]
    public async Task Same_content_yields_NotModified_even_after_rewrite()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("releases.json", FeedJson);
        var feed = new FileReleaseFeed(temp.Resolve("releases.json"));
        FeedResult first = await feed.GetAsync(null, CancellationToken.None);

        temp.WriteFile("releases.json", FeedJson);      // 重写同样内容，最后写入时间变了

        Assert.Same(FeedResult.NotModified, await feed.GetAsync(first.ETag, CancellationToken.None));
    }

    [Fact]
    public async Task Changed_content_yields_new_document_and_etag()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("releases.json", FeedJson);
        var feed = new FileReleaseFeed(temp.Resolve("releases.json"));
        FeedResult first = await feed.GetAsync(null, CancellationToken.None);

        temp.WriteFile("releases.json", FeedJson.Replace("1.2.4", "1.2.5", StringComparison.Ordinal));
        FeedResult second = await feed.GetAsync(first.ETag, CancellationToken.None);

        Assert.False(second.IsNotModified);
        Assert.NotEqual(first.ETag, second.ETag);
        Assert.Equal(new Version(1, 2, 5), Assert.Single(second.Document!.Releases).Version);
    }

    [Fact]
    public async Task Bom_prefixed_file_is_parsed()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("releases.json", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(FeedJson)]);

        FeedResult result = await new FileReleaseFeed(temp.Resolve("releases.json")).GetAsync(null, CancellationToken.None);

        Assert.Single(result.Document!.Releases);
    }

    [Fact]
    public async Task Missing_file_throws_FileNotFoundException()
    {
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new FileReleaseFeed(temp.Resolve("nope.json")).GetAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_directory_throws_DirectoryNotFoundException()
    {
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new FileReleaseFeed(temp.Resolve("missing/nope.json")).GetAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_file_throws_JsonException()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("releases.json", "{ nope");

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => new FileReleaseFeed(temp.Resolve("releases.json")).GetAsync(null, CancellationToken.None));
    }

    [Fact]
    public void Constructor_normalizes_relative_paths_and_rejects_blank()
    {
        Assert.ThrowsAny<ArgumentException>(() => new FileReleaseFeed(""));
        Assert.ThrowsAny<ArgumentException>(() => new FileReleaseFeed("   "));
        Assert.ThrowsAny<ArgumentException>(() => new FileReleaseFeed(null!));
        Assert.True(Path.IsPathRooted(new FileReleaseFeed("relative/releases.json").FeedPath));
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("releases.json", FeedJson);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileReleaseFeed(temp.Resolve("releases.json")).GetAsync(null, cts.Token));
    }
}
