using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public sealed class LocalAppDataOverrideTests
{
    private const string Exe = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe";

    private static UpdateEnvironment TestEnvironment() => new()
    {
        ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
        CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"],
        EntryAssemblyName = "MyApp",
        InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
    };

    [Fact]
    public void Default_is_null()
    {
        Assert.Null(new UpdateClientOptions { FeedUrl = "https://s/r.json" }.LocalAppDataDirectory);
    }

    [Fact]
    public void Null_resolves_to_the_real_known_folder()
    {
        // 这正是 e2e 不能用 LOCALAPPDATA 环境变量的原因：GetFolderPath 走 Known Folder API。
        ResolvedClientOptions r = ResolvedClientOptions.From(
            new UpdateClientOptions { FeedUrl = "https://s/r.json" }, TestEnvironment());

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            r.LocalAppDataDirectory);
    }

    [Fact]
    public void An_override_is_used_verbatim_after_GetFullPath()
    {
        using var dir = new TempDirectory();
        ResolvedClientOptions r = ResolvedClientOptions.From(
            new UpdateClientOptions { FeedUrl = "https://s/r.json", LocalAppDataDirectory = dir.Root },
            TestEnvironment());

        Assert.Equal(Path.GetFullPath(dir.Root), r.LocalAppDataDirectory);
        Assert.NotEqual(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            r.LocalAppDataDirectory);
    }

    [Fact]
    public void A_missing_directory_is_accepted_because_the_package_creates_it_on_demand()
    {
        string missing = Path.Combine(Path.GetTempPath(), "su-missing-" + Guid.NewGuid().ToString("N"));
        ResolvedClientOptions r = ResolvedClientOptions.From(
            new UpdateClientOptions { FeedUrl = "https://s/r.json", LocalAppDataDirectory = missing },
            TestEnvironment());

        Assert.Equal(missing, r.LocalAppDataDirectory);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_is_rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ResolvedClientOptions.From(
            new UpdateClientOptions { FeedUrl = "https://s/r.json", LocalAppDataDirectory = value },
            TestEnvironment()));

        Assert.Contains(nameof(UpdateClientOptions.LocalAppDataDirectory), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layout_built_from_the_override_never_touches_the_real_local_app_data()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        var layout = new UpdateLayout(install.Root, appData.Root, "MyApp-1a2b3c4d");

        Assert.StartsWith(appData.Root, layout.DownloadCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appData.Root, layout.FallbackLogDirectory, StringComparison.OrdinalIgnoreCase);
        // %TEMP% 本身就在真实 LocalAppData 之下，所以不能断言"路径不含真实目录"；应断言与真实根构造出的布局不同。
        var real = new UpdateLayout(install.Root, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp-1a2b3c4d");
        Assert.NotEqual(real.DownloadCacheDirectory, layout.DownloadCacheDirectory);
        Assert.NotEqual(real.FallbackLogDirectory, layout.FallbackLogDirectory);
    }

    [Fact]
    public void UpdateClient_builds_its_layout_from_the_resolved_override()
    {
        using var appData = new TempDirectory();
        UpdateEnvironment env = TestEnvironment();
        ResolvedClientOptions r = ResolvedClientOptions.From(
            new UpdateClientOptions { FeedUrl = "https://s/r.json", LocalAppDataDirectory = appData.Root }, env);

        UpdateLayout layout = UpdateClient.CreateLayout(r, env, r.AppId);

        Assert.Equal(env.InstallDirectory, layout.InstallDirectory);
        Assert.StartsWith(appData.Root, layout.DownloadCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appData.Root, layout.FallbackLogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupRunner_builds_its_layout_from_the_override()
    {
        using var appData = new TempDirectory();
        UpdateLayout? seen = null;
        var runner = new StartupRunner(
            new UpdateEnvironment
            {
                ProcessPath = Exe,
                CommandLineArguments = [Exe],
                EntryAssemblyName = "MyApp",
                InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
            },
            null,
            layout =>
            {
                seen = layout;
                return new FakeUpdateEngine();
            },
            appData.Root);

        runner.Run([]);

        Assert.NotNull(seen);
        Assert.StartsWith(appData.Root, seen.DownloadCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appData.Root, seen.FallbackLogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupRunner_rejects_a_blank_override()
    {
        var runner = new StartupRunner(new UpdateEnvironment(), null, _ => new FakeUpdateEngine(), "  ");

        ArgumentException ex = Assert.Throws<ArgumentException>(() => runner.Run([]));

        Assert.Contains("localAppDataDirectory", ex.Message, StringComparison.Ordinal);
    }
}
