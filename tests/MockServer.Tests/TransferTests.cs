using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

public sealed class TransferTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static byte[] MakePackage(int size)
    {
        byte[] bytes = new byte[size];
        new Random(4242).NextBytes(bytes);
        return bytes;
    }

    private async Task<byte[]> ArrangePackageAsync(string name, int size, CancellationToken cancellationToken)
    {
        await fixture.ResetAsync(cancellationToken);
        byte[] package = MakePackage(size);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", name), package);
        return package;
    }

    /// <summary>读到连接被切断为止，返回已经收到的字节。</summary>
    private static async Task<byte[]> ReadUntilCutAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (IOException)
        {
            // 预期：服务器在中途切断了连接。
        }
        catch (HttpRequestException)
        {
            // 某些栈上会包装成这个类型。
        }

        return received.ToArray();
    }

    [Fact]
    public async Task Download_is_cut_mid_stream_and_resumes_with_range()
    {
        byte[] package = await ArrangePackageAsync("cut.zip", 300_000, TestContext.Current.CancellationToken);

        using HttpResponseMessage armed = await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/cut.zip","cutAfterBytes":50000}"""),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, armed.StatusCode);

        using HttpResponseMessage cut = await fixture.Client.GetAsync(
            "packages/cut.zip", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        byte[] partial = await ReadUntilCutAsync(cut, TestContext.Current.CancellationToken);

        Assert.InRange(partial.Length, 1, package.Length - 1);
        Assert.Equal(package[..partial.Length], partial);

        // 撤掉切断，用 Range 续传剩下的部分。
        await fixture.Client.DeleteAsync("_control/transfer", TestContext.Current.CancellationToken);

        using var resume = new HttpRequestMessage(HttpMethod.Get, "packages/cut.zip");
        resume.Headers.Range = new RangeHeaderValue(partial.Length, null);
        using HttpResponseMessage rest =
            await fixture.Client.SendAsync(resume, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        byte[] tail = await rest.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        byte[] joined = [.. partial, .. tail];
        Assert.Equal(package, joined);
    }

    [Fact]
    public async Task Cut_delivers_exactly_the_requested_prefix()
    {
        byte[] package = await ArrangePackageAsync("cut-exact.zip", 200_000, TestContext.Current.CancellationToken);

        await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/cut-exact.zip","cutAfterBytes":40960}"""),
            TestContext.Current.CancellationToken);

        using HttpResponseMessage cut = await fixture.Client.GetAsync(
            "packages/cut-exact.zip", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        byte[] partial = await ReadUntilCutAsync(cut, TestContext.Current.CancellationToken);

        Assert.Equal(40960, partial.Length);
        Assert.Equal(package[..40960], partial);
    }

    [Fact]
    public async Task Throttling_slows_the_download_without_corrupting_it()
    {
        byte[] package = await ArrangePackageAsync("slow.zip", 200_000, TestContext.Current.CancellationToken);

        await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/slow.zip","bytesPerSecond":200000}"""),
            TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        byte[] downloaded =
            await fixture.Client.GetByteArrayAsync("packages/slow.zip", TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(package, downloaded);

        // 200000 字节 / 200000 字节每秒 ≈ 1 秒。给足余量只断言"明显慢于不限速"，
        // 不断言上界 —— 上界会让测试在负载高的机器上随机变红。
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 500,
            $"限速未生效：{stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Transfer_is_scoped_to_the_exact_path()
    {
        byte[] other = await ArrangePackageAsync("scoped-a.zip", 100_000, TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "scoped-b.zip"), other);

        await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/scoped-a.zip","cutAfterBytes":1024}"""),
            TestContext.Current.CancellationToken);

        byte[] untouched =
            await fixture.Client.GetByteArrayAsync("packages/scoped-b.zip", TestContext.Current.CancellationToken);

        Assert.Equal(other, untouched);
    }

    [Fact]
    public async Task Delete_removes_the_transfer_setting()
    {
        byte[] package = await ArrangePackageAsync("restore.zip", 100_000, TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/restore.zip","cutAfterBytes":1024}"""),
            TestContext.Current.CancellationToken);

        await fixture.Client.DeleteAsync("_control/transfer", TestContext.Current.CancellationToken);

        byte[] downloaded =
            await fixture.Client.GetByteArrayAsync("packages/restore.zip", TestContext.Current.CancellationToken);

        Assert.Equal(package, downloaded);
    }

    [Fact]
    public async Task Reset_removes_the_transfer_setting()
    {
        byte[] package = await ArrangePackageAsync("reset.zip", 100_000, TestContext.Current.CancellationToken);
        await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/packages/reset.zip","cutAfterBytes":1024}"""),
            TestContext.Current.CancellationToken);

        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        byte[] downloaded =
            await fixture.Client.GetByteArrayAsync("packages/reset.zip", TestContext.Current.CancellationToken);

        Assert.Equal(package, downloaded);
    }

    [Fact]
    public async Task Control_plane_is_never_throttled_or_cut()
    {
        await ArrangePackageAsync("ignored.zip", 1024, TestContext.Current.CancellationToken);

        using HttpResponseMessage armed = await fixture.Client.PostAsync(
            "_control/transfer",
            Json("""{"path":"/_control/reports","cutAfterBytes":1}"""),
            TestContext.Current.CancellationToken);

        // 先确认设置被接受：若设置被拒收（400），下面的断言无论豁免在不在都会通过，测试就形同虚设。
        Assert.Equal(HttpStatusCode.NoContent, armed.StatusCode);

        string reports =
            await fixture.Client.GetStringAsync("_control/reports", TestContext.Current.CancellationToken);

        Assert.Equal("[]", reports);
    }

    [Theory]
    [InlineData("""{"cutAfterBytes":100}""")]
    [InlineData("""{"path":"/packages/x.zip"}""")]
    [InlineData("""{"path":"/packages/x.zip","bytesPerSecond":-1}""")]
    [InlineData("""{"path":"/packages/x.zip","cutAfterBytes":-5}""")]
    [InlineData("nonsense")]
    public async Task Invalid_transfer_requests_are_rejected(string body)
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.PostAsync("_control/transfer", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
