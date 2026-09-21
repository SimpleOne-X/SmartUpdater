using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class TransportFactoryTests
{
    private static readonly UpdateEnvironment Env = new()
    {
        ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
        CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe"],
        EntryAssemblyName = "MyApp",
        InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
    };

    private static TransportSet Create(UpdateClientOptions options, RecordingLog? log = null)
        => TransportFactory.Create(ResolvedClientOptions.From(options, Env), log ?? new RecordingLog(), new ManualTimeProvider());

    [Fact]
    public void FeedUrl_builds_http_feed_downloader_with_base_uri_and_owns_the_client()
    {
        using TransportSet set = Create(new UpdateClientOptions { FeedUrl = "https://server/updates/releases.json" });

        var feed = Assert.IsType<HttpReleaseFeed>(set.Feed);
        var downloader = Assert.IsType<HttpPackageDownloader>(set.Downloader);
        Assert.Equal(new Uri("https://server/updates/releases.json"), feed.FeedUri);
        Assert.Equal(feed.FeedUri, downloader.BaseUri);
        Assert.Null(set.Reporter);
        Assert.NotNull(set.OwnedHttpClient);
    }

    [Fact]
    public void ReportUrl_builds_http_reporter_on_the_same_client()
    {
        using TransportSet set = Create(new UpdateClientOptions { FeedUrl = "https://server/updates/releases.json", ReportUrl = "https://server/api/v1/update-reports" });

        var reporter = Assert.IsType<HttpUpdateReporter>(set.Reporter);
        Assert.Equal(new Uri("https://server/api/v1/update-reports"), reporter.ReportUri);
    }

    [Fact]
    public void File_feed_gets_the_file_downloader_rooted_at_the_feed_directory()
    {
        var fileFeed = new FileReleaseFeed(@"C:\share\updates\releases.json");

        using TransportSet set = Create(new UpdateClientOptions { Feed = fileFeed });

        Assert.Same(fileFeed, set.Feed);
        var downloader = Assert.IsType<FilePackageDownloader>(set.Downloader);
        Assert.Equal(@"C:\share\updates", downloader.BaseDirectory);
        Assert.Null(set.OwnedHttpClient);
    }

    [Fact]
    public void Custom_feed_without_downloader_gets_http_downloader_without_base_uri()
    {
        using TransportSet set = Create(new UpdateClientOptions { Feed = new FakeReleaseFeed() });

        var downloader = Assert.IsType<HttpPackageDownloader>(set.Downloader);
        Assert.Null(downloader.BaseUri);
        Assert.NotNull(set.OwnedHttpClient);
    }

    [Fact]
    public void Custom_transports_are_passed_through_and_no_client_is_created()
    {
        var feed = new FakeReleaseFeed();
        var downloader = new FakePackageDownloader();
        var reporter = new RecordingReporter();

        using TransportSet set = Create(new UpdateClientOptions { Feed = feed, Downloader = downloader, Reporter = reporter });

        Assert.Same(feed, set.Feed);
        Assert.Same(downloader, set.Downloader);
        Assert.Same(reporter, set.Reporter);
        Assert.Null(set.OwnedHttpClient);
    }

    [Fact]
    public void Untrusted_certificates_flag_logs_a_warning_and_installs_the_callback()
    {
        var log = new RecordingLog();

        using TransportSet set = Create(new UpdateClientOptions { FeedUrl = "https://server/r.json", AllowUntrustedCertificates = true }, log);

        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
        using SocketsHttpHandler relaxed = TransportFactory.CreateHandler(allowUntrustedCertificates: true);
        using SocketsHttpHandler strict = TransportFactory.CreateHandler(allowUntrustedCertificates: false);
        Assert.NotNull(relaxed.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(strict.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(TimeSpan.FromMinutes(5), strict.PooledConnectionLifetime);
    }

    [Fact]
    public void Untrusted_certificates_flag_without_an_owned_client_warns_that_it_has_no_effect()
    {
        var log = new RecordingLog();

        using TransportSet set = Create(new UpdateClientOptions { Feed = new FakeReleaseFeed(), Downloader = new FakePackageDownloader(), AllowUntrustedCertificates = true }, log);

        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains("无效", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_client_has_a_100_second_timeout()
    {
        using HttpClient client = TransportFactory.CreateHttpClient(false);

        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);
    }
}
