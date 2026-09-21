using System.Net;
using System.Text;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// 被限速的响应不会自己结束。Kestrel 停机时默认要等它 <c>HostOptions.ShutdownTimeout</c>（30 秒），
/// 所以 <see cref="MockServerHost.DisposeAsync"/> 必须自己给停机设一个短的上限。
/// </summary>
public sealed class TransferShutdownTests
{
    private const int PackageSize = 1_000_000;

    // 测试期间用的停机上限。远小于下面等待 Dispose 的 15 秒，也远小于 Kestrel 默认的 30 秒：
    // 只有 Dispose 真的按这个上限强制收场，测试才过得去。
    private static readonly TimeSpan StopBudget = TimeSpan.FromMilliseconds(500);

    // 等 Dispose 的墙钟上限。要远大于 StopBudget（负载高的机器上不能误报），又要小于 30 秒（否则抓不到"无界停机"）。
    private static readonly TimeSpan DisposeDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Disposing_the_host_while_a_throttled_response_is_in_flight_completes_quickly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory();
        dir.WriteBytes("packages/slow.zip", new byte[PackageSize]);

        var host = new MockServerHost(new MockServerOptions(dir.Path, 0), StopBudget);
        try
        {
            await host.StartAsync(cancellationToken);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };

            // 每秒 4096 字节：1 MB 的文件要传四分钟，绝不会在测试期间自己结束。
            using HttpResponseMessage armed = await client.PostAsync(
                "_control/transfer",
                new StringContent("""{"path":"/packages/slow.zip","bytesPerSecond":4096}""", Encoding.UTF8, "application/json"),
                cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, armed.StatusCode);

            using HttpResponseMessage response = await client.GetAsync(
                "packages/slow.zip", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

            // 读到第一个字节，说明响应确实已经在途中；用"数据到了"做同步，不用睡眠。
            byte[] first = new byte[1];
            Assert.Equal(1, await body.ReadAsync(first, cancellationToken));

            // 无界停机会在这里等满 30 秒；WaitAsync 到 15 秒就抛 TimeoutException，测试变红而不是卡住。
            await host.DisposeAsync().AsTask().WaitAsync(DisposeDeadline, cancellationToken);

            // 在途的响应被中断了：客户端要么读到连接被关闭，要么读到流提前结束，总之凑不齐整个文件。
            long received = 1;
            try
            {
                byte[] buffer = new byte[4096];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                }
            }
            catch (IOException)
            {
                // 预期：服务器强制中断了这条连接。
            }

            Assert.True(received < PackageSize, $"响应竟然完整送达了：{received} 字节");
        }
        finally
        {
            // DisposeAsync 幂等；上面若在 Dispose 之前就断言失败，这里保证服务器被收掉。
            await host.DisposeAsync().AsTask().WaitAsync(DisposeDeadline, CancellationToken.None);
        }
    }

    [Fact]
    public void Default_stop_timeout_is_a_few_seconds_not_the_30_second_host_default()
    {
        Assert.InRange(MockServerHost.DefaultStopTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_stop_timeout_is_rejected(int milliseconds)
    {
        using var dir = new TempDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MockServerHost(new MockServerOptions(dir.Path, 0), TimeSpan.FromMilliseconds(milliseconds)));
    }
}
