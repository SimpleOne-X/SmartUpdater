using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class FilePackageDownloaderTests
{
    private static readonly byte[] Body = Enumerable.Range(0, 150_000).Select(i => (byte)(i % 251)).ToArray();

    private sealed class Collector : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Copies_relative_package_from_share_directory_with_progress()
    {
        using var share = new TempDirectory();
        using var cache = new TempDirectory();
        share.WriteBytes("packages/App-1.2.4.zip", Body);
        var downloader = new FilePackageDownloader(share.Root, new ManualTimeProvider());
        var progress = new Collector();
        ReleaseEntry release = FeedFixtures.Release("1.2.4", url: "packages/App-1.2.4.zip", size: Body.Length);

        string result = await downloader.DownloadAsync(release, cache.Resolve("updates/1.2.4.0.zip"), progress, CancellationToken.None);

        Assert.Equal(cache.Resolve("updates/1.2.4.0.zip"), result);
        Assert.Equal(Body, File.ReadAllBytes(result));
        Assert.False(cache.Exists("updates/1.2.4.0.zip.part"));
        Assert.Equal(Body.Length, progress.Reports[^1].BytesReceived);
        Assert.Equal(100, progress.Reports[^1].Percent);
    }

    [Fact]
    public async Task Resumes_from_existing_part_file()
    {
        using var share = new TempDirectory();
        using var cache = new TempDirectory();
        share.WriteBytes("p.zip", Body);
        cache.WriteBytes("p.zip.part", Body[..40_000]);
        var progress = new Collector();

        await new FilePackageDownloader(share.Root, new ManualTimeProvider()).DownloadAsync(FeedFixtures.Release("1.0", url: "p.zip", size: Body.Length), cache.Resolve("p.zip"), progress, CancellationToken.None);

        Assert.Equal(Body, File.ReadAllBytes(cache.Resolve("p.zip")));
        Assert.True(progress.Reports[0].BytesReceived > 40_000);
    }

    [Fact]
    public async Task Rooted_path_and_file_uri_are_used_as_is()
    {
        using var share = new TempDirectory();
        using var cache = new TempDirectory();
        share.WriteBytes("p.zip", Body);
        var downloader = new FilePackageDownloader(@"C:\unrelated", new ManualTimeProvider());

        await downloader.DownloadAsync(FeedFixtures.Release("1.0", url: share.Resolve("p.zip"), size: Body.Length), cache.Resolve("a.zip"), null, CancellationToken.None);
        await downloader.DownloadAsync(FeedFixtures.Release("1.0", url: new Uri(share.Resolve("p.zip")).AbsoluteUri, size: Body.Length), cache.Resolve("b.zip"), null, CancellationToken.None);

        Assert.Equal(Body, File.ReadAllBytes(cache.Resolve("a.zip")));
        Assert.Equal(Body, File.ReadAllBytes(cache.Resolve("b.zip")));
    }

    [Fact]
    public void ResolvePackagePath_normalizes_forward_slashes()
    {
        string resolved = FilePackageDownloader.ResolvePackagePath(@"\\server\share\updates", "packages/x.zip");

        Assert.Equal(@"\\server\share\updates\packages\x.zip", resolved);
        Assert.Equal(@"D:\pkgs\x.zip", FilePackageDownloader.ResolvePackagePath(@"\\server\share", @"D:\pkgs\x.zip"));
        Assert.Equal(@"D:\pkgs\x.zip", FilePackageDownloader.ResolvePackagePath(@"\\server\share", "file:///D:/pkgs/x.zip"));
    }

    [Fact]
    public async Task Missing_source_throws_FileNotFoundException_and_leaves_no_part()
    {
        using var share = new TempDirectory();
        using var cache = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new FilePackageDownloader(share.Root, new ManualTimeProvider()).DownloadAsync(FeedFixtures.Release("1.0", url: "missing.zip"), cache.Resolve("m.zip"), null, CancellationToken.None));

        Assert.False(cache.Exists("m.zip.part"));
    }

    [Fact]
    public async Task Cancelled_token_throws_and_keeps_part_file()
    {
        using var share = new TempDirectory();
        using var cache = new TempDirectory();
        share.WriteBytes("p.zip", Body);
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FilePackageDownloader(share.Root, new ManualTimeProvider()).DownloadAsync(FeedFixtures.Release("1.0", url: "p.zip", size: Body.Length), cache.Resolve("p.zip"), progress, cts.Token));

        Assert.True(cache.Exists("p.zip.part"));
        Assert.False(cache.Exists("p.zip"));
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => cts.Cancel();
    }

    [Fact]
    public void Constructor_rejects_blank_base_directory()
    {
        Assert.ThrowsAny<ArgumentException>(() => new FilePackageDownloader("", new ManualTimeProvider()));
        Assert.Throws<ArgumentNullException>(() => new FilePackageDownloader(@"C:\x", null!));
    }
}
