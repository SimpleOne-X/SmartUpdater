namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>进程内起一个 MockServer，配一个指向它的 HttpClient，并给每个测试一块干净的临时 root。</summary>
public sealed class MockServerFixture : IAsyncLifetime
{
    private TempDirectory _dir = null!;
    private MockServerHost _host = null!;

    public HttpClient Client { get; private set; } = null!;

    public string Root => _dir.Path;

    // MockServerHost 是 internal，所以这个属性也只能是 internal（否则 CS0053）。
    internal MockServerHost Host => _host;

    public async ValueTask InitializeAsync()
    {
        _dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(_dir.Path, "packages"));
        _host = new MockServerHost(new MockServerOptions(_dir.Path, 0));
        await _host.StartAsync(CancellationToken.None);
        Client = new HttpClient { BaseAddress = _host.BaseAddress };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.DisposeAsync();
        _dir.Dispose();
    }

    /// <summary>把写进 root 的文件与服务器的可变状态都清回初始态。</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.PostAsync("_control/reset", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
