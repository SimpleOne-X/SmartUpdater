using System.Net;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public async Task WebApplication_can_bind_a_dynamic_loopback_port()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.SuppressStatusMessages(true);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));

        WebApplication app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            string url = Assert.Single(app.Urls);
            Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
            Assert.DoesNotContain(":0", url[^6..], StringComparison.Ordinal);

            using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
            Assert.Equal("pong", await http.GetStringAsync("ping", TestContext.Current.CancellationToken));
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
