using System.Net;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class MockServerOptionsTests
{
    [Fact]
    public void Root_is_required()
    {
        Assert.False(MockServerOptions.TryParse([], out MockServerOptions? options, out string? error));
        Assert.Null(options);
        Assert.Contains("--root", error);
    }

    [Fact]
    public void Port_defaults_to_zero()
    {
        using var dir = new TempDirectory();
        Assert.True(MockServerOptions.TryParse(["--root", dir.Path], out MockServerOptions? options, out string? error));
        Assert.Null(error);
        Assert.Equal(0, options!.Port);
        Assert.Equal(dir.Path, options.Root);
    }

    [Fact]
    public void Explicit_port_is_accepted()
    {
        using var dir = new TempDirectory();
        Assert.True(MockServerOptions.TryParse(["--root", dir.Path, "--port", "18080"], out MockServerOptions? options, out _));
        Assert.Equal(18080, options!.Port);
    }

    [Fact]
    public void Missing_root_directory_is_rejected()
    {
        string missing = Path.Combine(Path.GetTempPath(), "sumock-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(MockServerOptions.TryParse(["--root", missing], out _, out string? error));
        Assert.Contains(missing, error);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void Out_of_range_or_non_numeric_port_is_rejected(string port)
    {
        using var dir = new TempDirectory();
        Assert.False(MockServerOptions.TryParse(["--root", dir.Path, "--port", port], out _, out string? error));
        Assert.Contains("--port", error);
    }

    [Fact]
    public void Unknown_option_is_rejected()
    {
        using var dir = new TempDirectory();
        Assert.False(MockServerOptions.TryParse(["--root", dir.Path, "--verbose"], out _, out string? error));
        Assert.Contains("--verbose", error);
    }

    [Fact]
    public void Option_without_value_is_rejected()
    {
        Assert.False(MockServerOptions.TryParse(["--root"], out _, out string? error));
        Assert.Contains("--root", error);
    }

    // 重复选项一律报错。
    [Theory]
    [InlineData("--root")]
    [InlineData("--port")]
    public void Repeated_option_is_rejected(string option)
    {
        using var dir = new TempDirectory();
        string value = option == "--root" ? dir.Path : "18080";
        Assert.False(MockServerOptions.TryParse(["--root", dir.Path, "--port", "18080", option, value], out _, out string? error));
        Assert.Contains(option, error);
    }
}

public sealed class MockServerHostTests
{
    [Fact]
    public async Task Host_binds_a_real_loopback_port_and_reports_it()
    {
        using var dir = new TempDirectory();
        await using var host = new MockServerHost(new MockServerOptions(dir.Path, 0));
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("127.0.0.1", host.BaseAddress.Host);
        Assert.InRange(host.BaseAddress.Port, 1, 65535);
    }

    [Fact]
    public async Task Two_hosts_started_at_once_get_different_ports()
    {
        using var dirA = new TempDirectory();
        using var dirB = new TempDirectory();
        await using var a = new MockServerHost(new MockServerOptions(dirA.Path, 0));
        await using var b = new MockServerHost(new MockServerOptions(dirB.Path, 0));

        await a.StartAsync(TestContext.Current.CancellationToken);
        await b.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(a.BaseAddress.Port, b.BaseAddress.Port);
    }

    [Fact]
    public void Listening_line_has_the_exact_shape_the_e2e_script_greps_for()
    {
        string line = MockServerHost.FormatListeningLine(new Uri("http://127.0.0.1:56557/"));
        Assert.Equal("LISTENING http://127.0.0.1:56557/", line);
    }
}
