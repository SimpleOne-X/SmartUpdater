using System.Security.Cryptography;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateClientOptionsTests
{
    private static UpdateEnvironment Env() => new()
    {
        ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
        CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"],
        EntryAssemblyName = "MyApp",
        InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
    };

    private static UpdateClientOptions Minimal() => new() { FeedUrl = "https://server/updates/releases.json" };

    [Fact]
    public void Defaults_come_from_ClientPolicyLimits_and_decision_4()
    {
        var o = new UpdateClientOptions();

        Assert.Equal(ClientPolicyLimits.Defaults.PollInterval, o.PollInterval);
        Assert.Equal(ClientPolicyLimits.Defaults.JitterWindow, o.JitterWindow);
        Assert.Equal(ClientPolicyLimits.Defaults.HeartbeatInterval, o.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(300), o.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(600), o.JitterWindow);
        Assert.Equal(TimeSpan.FromSeconds(21600), o.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), o.ShutdownTimeout);
        Assert.False(o.AllowUntrustedCertificates);
        Assert.False(o.AllowVersionDowngrade);
        Assert.Null(o.PublicKey);
        Assert.True(o.EnableFileLogging);
        Assert.True(o.IncludeLogTailOnFailure);
        Assert.Null(o.LogCallback);
        Assert.Null(o.AppId);
        Assert.Null(o.ExecutablePath);
        Assert.Null(o.CurrentVersion);
        Assert.Null(o.Feed);
        Assert.Null(o.FeedUrl);
        Assert.Null(o.Downloader);
        Assert.Null(o.Reporter);
        Assert.Null(o.ReportUrl);
    }

    [Fact]
    public void Minimal_options_resolve_with_derived_app_id_and_clamped_settings()
    {
        ResolvedClientOptions r = ResolvedClientOptions.From(Minimal(), Env());

        Assert.Equal(AppIdentity.Derive("MyApp", @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe"), r.AppId);
        Assert.Equal(new Uri("https://server/updates/releases.json"), r.FeedUri);
        Assert.Null(r.ReportUri);
        Assert.Equal(ClientPolicyLimits.Defaults, r.LocalSettings);
        Assert.Equal(TimeSpan.FromSeconds(15), r.ShutdownTimeout);
        Assert.Null(r.PublicKey);
        Assert.Null(r.CurrentVersion);
        Assert.False(r.Identity.IsDotnetHost);
    }

    [Fact]
    public void Feed_instance_is_accepted_instead_of_url()
    {
        ResolvedClientOptions r = ResolvedClientOptions.From(new UpdateClientOptions { Feed = new FakeReleaseFeed() }, Env());

        Assert.Null(r.FeedUri);
        Assert.NotNull(r.Source.Feed);
    }

    [Fact]
    public void Feed_and_FeedUrl_must_be_set_exactly_once()
    {
        var neither = Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions(), Env()));
        var both = Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { Feed = new FakeReleaseFeed(), FeedUrl = "https://s/r.json" }, Env()));

        Assert.Contains("Feed", neither.Message, StringComparison.Ordinal);
        Assert.Contains("Feed", both.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("releases.json")]
    [InlineData("ftp://server/releases.json")]
    [InlineData("   ")]
    public void Invalid_FeedUrl_is_rejected(string url)
    {
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = url }, Env()));
    }

    [Fact]
    public void ReportUrl_is_parsed_and_conflicts_with_Reporter()
    {
        ResolvedClientOptions r = ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ReportUrl = "https://s/api/v1/update-reports" }, Env());
        Assert.Equal(new Uri("https://s/api/v1/update-reports"), r.ReportUri);

        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ReportUrl = "not a url" }, Env()));
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ReportUrl = "https://s/api", Reporter = new RecordingReporter() }, Env()));
    }

    [Fact]
    public void Intervals_are_clamped_to_floors_and_ceilings()
    {
        var o = new UpdateClientOptions { FeedUrl = "https://s/r.json", PollInterval = TimeSpan.FromSeconds(1), JitterWindow = TimeSpan.FromDays(2), HeartbeatInterval = TimeSpan.FromSeconds(10) };

        ResolvedClientOptions r = ResolvedClientOptions.From(o, Env());

        Assert.Equal(ClientPolicyLimits.MinPollInterval, r.LocalSettings.PollInterval);
        Assert.Equal(ClientPolicyLimits.MaxJitterWindow, r.LocalSettings.JitterWindow);
        Assert.Equal(ClientPolicyLimits.MinHeartbeatInterval, r.LocalSettings.HeartbeatInterval);
    }

    [Fact]
    public void Negative_intervals_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", PollInterval = TimeSpan.FromSeconds(-1) }, Env()));
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", JitterWindow = TimeSpan.FromSeconds(-1) }, Env()));
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", HeartbeatInterval = TimeSpan.FromSeconds(-1) }, Env()));
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ShutdownTimeout = TimeSpan.FromSeconds(-1) }, Env()));
    }

    [Fact]
    public void ShutdownTimeout_is_capped_at_ten_minutes_and_zero_is_allowed()
    {
        Assert.Equal(ResolvedClientOptions.MaxShutdownTimeout, ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ShutdownTimeout = TimeSpan.FromHours(1) }, Env()).ShutdownTimeout);
        Assert.Equal(TimeSpan.Zero, ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ShutdownTimeout = TimeSpan.Zero }, Env()).ShutdownTimeout);
    }

    [Fact]
    public void Valid_public_key_is_kept_verbatim()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string spki = ReleaseSignature.ExportPublicKey(key);

        ResolvedClientOptions r = ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", PublicKey = spki }, Env());

        Assert.Equal(spki, r.PublicKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Invalid_public_key_is_rejected_at_construction(string publicKey)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", PublicKey = publicKey }, Env()));

        Assert.Equal("PublicKey", ex.ParamName);
    }

    [Fact]
    public void Non_p256_public_key_is_rejected()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string spki = Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo());

        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", PublicKey = spki }, Env()));
    }

    [Fact]
    public void CurrentVersion_is_normalized()
    {
        ResolvedClientOptions r = ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", CurrentVersion = new Version(1, 2) }, Env());

        Assert.Equal(new Version(1, 2, 0, 0), r.CurrentVersion);
    }

    [Fact]
    public void Explicit_app_id_and_executable_path_are_applied()
    {
        var o = new UpdateClientOptions { FeedUrl = "https://s/r.json", AppId = "Contoso.Payroll", ExecutablePath = @"C:\Users\x\AppData\Local\MyApp\app\Payroll.exe" };

        ResolvedClientOptions r = ResolvedClientOptions.From(o, Env());

        Assert.Equal("Contoso.Payroll", r.AppId);
        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\Payroll.exe", r.Identity.ExecutablePath);
        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\Payroll.exe", r.Identity.LaunchFileName);
    }

    [Fact]
    public void Invalid_explicit_app_id_and_relative_executable_path_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", AppId = ".." }, Env()));
        Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(new UpdateClientOptions { FeedUrl = "https://s/r.json", ExecutablePath = "MyApp.exe" }, Env()));
    }

    [Fact]
    public void From_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ResolvedClientOptions.From(null!, Env()));
        Assert.Throws<ArgumentNullException>(() => ResolvedClientOptions.From(Minimal(), null!));
    }
}
