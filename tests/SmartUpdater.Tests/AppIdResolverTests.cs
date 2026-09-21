using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class AppIdResolverTests
{
    private static UpdateEnvironment AppHost() => new()
    {
        ProcessPath = @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe",
        CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"],
        EntryAssemblyName = "MyApp",
        InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
    };

    private static UpdateEnvironment DotnetHost() => new()
    {
        ProcessPath = @"C:\Program Files\dotnet\dotnet.exe",
        CommandLineArguments = [@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll", "--verbose"],
        EntryAssemblyName = "MyApp",
        InstallDirectory = @"C:\Users\x\AppData\Local\MyApp\app",
    };

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
    [InlineData(@"C:\tools\DOTNET.EXE", true)]
    [InlineData("dotnet", true)]
    [InlineData(@"C:\app\MyApp.exe", false)]
    [InlineData(@"C:\app\dotnet-tool.exe", false)]
    [InlineData(null, false)]
    public void IsDotnetHost_looks_at_the_file_name_only(string? processPath, bool expected)
    {
        Assert.Equal(expected, AppIdResolver.IsDotnetHost(processPath));
    }

    [Fact]
    public void Apphost_identity_uses_the_exe_for_everything()
    {
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(AppHost(), null);

        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe", id.ExecutablePath);
        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe", id.LaunchFileName);
        Assert.Equal("", id.LaunchArgumentPrefix);
        Assert.False(id.IsDotnetHost);
        Assert.Equal("MyApp.exe", id.RelativeExecutablePath(@"C:\Users\x\AppData\Local\MyApp\app"));
    }

    [Fact]
    public void Dotnet_host_identity_hashes_the_dll_and_restarts_through_dotnet()
    {
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(DotnetHost(), null);

        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll", id.ExecutablePath);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", id.LaunchFileName);
        Assert.Equal(CommandLine.Quote(@"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"), id.LaunchArgumentPrefix);
        Assert.True(id.IsDotnetHost);
        Assert.Equal("MyApp.dll", id.RelativeExecutablePath(@"C:\Users\x\AppData\Local\MyApp\app"));
    }

    [Fact]
    public void Dll_override_under_dotnet_host_keeps_the_host_but_uses_the_given_dll()
    {
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(DotnetHost(), @"C:\Users\x\AppData\Local\MyApp\app\Real.dll");

        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\Real.dll", id.ExecutablePath);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", id.LaunchFileName);
        Assert.Equal(CommandLine.Quote(@"C:\Users\x\AppData\Local\MyApp\app\Real.dll"), id.LaunchArgumentPrefix);
        Assert.True(id.IsDotnetHost);
    }

    [Fact]
    public void Exe_override_is_launched_directly_even_under_dotnet_host()
    {
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(DotnetHost(), @"C:\Users\x\AppData\Local\MyApp\app\Launcher.exe");

        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\Launcher.exe", id.ExecutablePath);
        Assert.Equal(@"C:\Users\x\AppData\Local\MyApp\app\Launcher.exe", id.LaunchFileName);
        Assert.Equal("", id.LaunchArgumentPrefix);
        Assert.False(id.IsDotnetHost);
    }

    [Fact]
    public void Missing_process_path_without_override_throws()
    {
        var env = new UpdateEnvironment { ProcessPath = null, CommandLineArguments = [], EntryAssemblyName = "MyApp" };

        var ex = Assert.Throws<InvalidOperationException>(() => AppIdResolver.ResolveProcessIdentity(env, null));

        Assert.Contains("ExecutablePath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotnet_host_with_relative_assembly_path_throws()
    {
        var env = new UpdateEnvironment { ProcessPath = @"C:\Program Files\dotnet\dotnet.exe", CommandLineArguments = ["MyApp.dll"], EntryAssemblyName = "MyApp" };

        Assert.Throws<InvalidOperationException>(() => AppIdResolver.ResolveProcessIdentity(env, null));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\MyApp\app", @"C:\Users\x\AppData\Local\MyApp\app\bin\MyApp.exe", "bin/MyApp.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\MyApp\app\", @"c:\users\X\appdata\local\myapp\APP\MyApp.exe", "MyApp.exe")]
    public void RelativeExecutablePath_uses_forward_slashes_and_ignores_case(string install, string exe, string expected)
    {
        var id = new ProcessIdentity(exe, exe, "", false);

        Assert.Equal(expected, id.RelativeExecutablePath(install));
    }

    [Fact]
    public void RelativeExecutablePath_outside_the_install_directory_throws()
    {
        var id = new ProcessIdentity(@"D:\elsewhere\MyApp.exe", @"D:\elsewhere\MyApp.exe", "", false);

        Assert.Throws<InvalidOperationException>(() => id.RelativeExecutablePath(@"C:\Users\x\AppData\Local\MyApp\app"));
        Assert.Throws<InvalidOperationException>(() => new ProcessIdentity(@"C:\app\..\other\x.exe", "", "", false).RelativeExecutablePath(@"C:\app"));
    }

    [Fact]
    public void Default_app_id_is_derived_from_assembly_name_and_executable_path()
    {
        UpdateEnvironment env = AppHost();
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(env, null);

        string appId = AppIdResolver.ResolveAppId(env, null, id);

        Assert.Equal(AppIdentity.Derive("MyApp", id.ExecutablePath), appId);
        Assert.StartsWith("MyApp-", appId, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotnet_host_default_app_id_hashes_the_dll_not_the_host()
    {
        UpdateEnvironment env = DotnetHost();
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(env, null);

        Assert.Equal(AppIdentity.Derive("MyApp", @"C:\Users\x\AppData\Local\MyApp\app\MyApp.dll"), AppIdResolver.ResolveAppId(env, null, id));
        Assert.NotEqual(AppIdentity.Derive("MyApp", @"C:\Program Files\dotnet\dotnet.exe"), AppIdResolver.ResolveAppId(env, null, id));
    }

    [Fact]
    public void Explicit_app_id_is_validated_and_returned_verbatim()
    {
        UpdateEnvironment env = AppHost();
        ProcessIdentity id = AppIdResolver.ResolveProcessIdentity(env, null);

        Assert.Equal("Contoso.Payroll", AppIdResolver.ResolveAppId(env, "Contoso.Payroll", id));
    }

    [Theory]
    [InlineData("My App", "ASCII")]
    [InlineData("我的应用", "ASCII")]
    [InlineData("", "ASCII")]
    [InlineData(".", "点号")]
    [InlineData("..", "点号")]
    [InlineData("...", "点号")]
    [InlineData("CON", "保留")]
    [InlineData("com1", "保留")]
    [InlineData("LPT9.app", "保留")]
    [InlineData("MyApp.", "结尾")]
    [InlineData("MyApp-", null)]
    public void Validate_rejects_invalid_app_ids(string appId, string? expectedWord)
    {
        if (expectedWord is null)
        {
            AppIdResolver.Validate(appId);      // 以 '-' 结尾是合法的（Derive 的产出形如 MyApp-f4bc3658）
            return;
        }

        var ex = Assert.Throws<ArgumentException>(() => AppIdResolver.Validate(appId));
        Assert.Contains(expectedWord, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_ids_longer_than_64_characters_and_accepts_64()
    {
        AppIdResolver.Validate(new string('a', 64));

        var ex = Assert.Throws<ArgumentException>(() => AppIdResolver.Validate(new string('a', 65)));
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Derived_ids_always_pass_validation()
    {
        AppIdResolver.Validate(AppIdentity.Derive("My App", @"C:\x\a.exe"));
        AppIdResolver.Validate(AppIdentity.Derive("", @"C:\x\a.exe"));
        AppIdResolver.Validate(AppIdentity.Derive("CON", @"C:\x\a.exe"));
    }
}
