using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateLayoutTests
{
    private const string AppId = "MyApp-1a2b3c4d";

    [Fact]
    public void Paths_follow_section_4_4()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        string state = Path.Combine(install.Root, ".smartupdater");
        Assert.Equal(install.Root, layout.InstallDirectory);
        Assert.Equal(state, layout.StateDirectory);
        Assert.Equal(Path.Combine(state, "state.json"), layout.StateFile);
        Assert.Equal(Path.Combine(state, "manifest.json"), layout.ManifestFile);
        Assert.Equal(Path.Combine(state, "journal.json"), layout.JournalFile);
        Assert.Equal(Path.Combine(state, "reports.jsonl"), layout.ReportsFile);
        Assert.Equal(Path.Combine(state, "logs"), layout.LogDirectory);
        Assert.Equal(Path.Combine(appData.Root, AppId), layout.AppDataDirectory);
        Assert.Equal(Path.Combine(appData.Root, AppId, "updates"), layout.DownloadCacheDirectory);
        Assert.Equal(Path.Combine(appData.Root, AppId, "logs"), layout.FallbackLogDirectory);
        Assert.Equal(AppId, layout.AppId);
    }

    [Fact]
    public void Trailing_separator_on_install_directory_is_normalized()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        var layout = new UpdateLayout(install.Root + Path.DirectorySeparatorChar, appData.Root, AppId);

        Assert.Equal(install.Root, layout.InstallDirectory);
    }

    [Fact]
    public void ResolveInstallPath_maps_forward_slashes_into_the_install_directory()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        Assert.Equal(Path.Combine(install.Root, "Resources", "logo.png"), layout.ResolveInstallPath("Resources/logo.png"));
        Assert.Equal(Path.Combine(install.Root, "MyApp.exe"), layout.ResolveInstallPath("MyApp.exe"));
        Assert.Equal(layout.ManifestFile, layout.ResolveInstallPath(UpdateLayout.ManifestRelativePath));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("sub/../../x")]
    [InlineData(@"C:\Windows\x")]
    [InlineData("/abs")]
    [InlineData(@"\\server\share\x")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveInstallPath_rejects_paths_that_escape_the_install_directory(string relative)
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        Assert.Throws<ArgumentException>(() => layout.ResolveInstallPath(relative));
    }

    [Fact]
    public void ResolveInstallPath_rejects_the_install_directory_itself()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        Assert.Throws<ArgumentException>(() => layout.ResolveInstallPath("sub/.."));
    }

    // 解析结果带尾部分隔符时（"sub/../"、"."）同样等于安装目录本身，必须一并拒绝。
    [Theory]
    [InlineData("sub/../")]
    [InlineData(".")]
    [InlineData("./")]
    public void ResolveInstallPath_rejects_paths_that_resolve_to_the_install_directory_with_a_trailing_separator(string relative)
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        Assert.Throws<ArgumentException>(() => layout.ResolveInstallPath(relative));
    }

    // 同名前缀的兄弟目录（<install>2）不是安装目录的子路径，只比较字符串前缀会放过它。
    [Fact]
    public void ResolveInstallPath_rejects_a_sibling_directory_that_shares_the_install_directory_name_as_prefix()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);
        string siblingRelative = "../" + Path.GetFileName(install.Root) + "2/x.dll";

        Assert.Throws<ArgumentException>(() => layout.ResolveInstallPath(siblingRelative));
    }

    // 安装目录是盘符根时，GetFullPath 后本身已带分隔符，前缀不能再补一个（只做路径计算，不碰磁盘）。
    [Fact]
    public void ResolveInstallPath_works_when_the_install_directory_is_a_drive_root()
    {
        using var appData = new TempDirectory();
        string root = Path.GetPathRoot(appData.Root)!;
        var layout = new UpdateLayout(root, appData.Root, AppId);

        Assert.Equal(Path.Combine(root, "sub", "x.dll"), layout.ResolveInstallPath("sub/x.dll"));
        Assert.Throws<ArgumentException>(() => layout.ResolveInstallPath("sub/.."));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("con.x")]
    [InlineData("My App")]
    [InlineData("MyApp.")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Invalid_app_ids_are_rejected(string appId)
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Assert.Throws<ArgumentException>(() => new UpdateLayout(install.Root, appData.Root, appId));
    }

    // 构造函数对安装目录与 LocalAppData 目录同样做空白检查。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_install_directory_is_rejected(string installDirectory)
    {
        using var appData = new TempDirectory();

        Assert.Throws<ArgumentException>(() => new UpdateLayout(installDirectory, appData.Root, AppId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_local_app_data_directory_is_rejected(string localAppDataDirectory)
    {
        using var install = new TempDirectory();

        Assert.Throws<ArgumentException>(() => new UpdateLayout(install.Root, localAppDataDirectory, AppId));
    }

    [Fact]
    public void Derived_app_id_is_accepted()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        string derived = AppIdentity.Derive("MyApp", Path.Combine(install.Root, "MyApp.exe"));

        var layout = new UpdateLayout(install.Root, appData.Root, derived);

        Assert.Equal(derived, layout.AppId);
    }

    [Fact]
    public void Download_path_is_version_named_zip_in_the_cache_directory()
    {
        using var install = new TempDirectory();
        using var appData = new TempDirectory();
        var layout = new UpdateLayout(install.Root, appData.Root, AppId);

        Assert.Equal(Path.Combine(layout.DownloadCacheDirectory, "1.2.4.zip"), layout.GetDownloadPath(new Version(1, 2, 4)));
    }
}
