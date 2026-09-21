using System.Net;
using System.Net.Http.Headers;
using static SimpleOneX.SmartUpdater.MockServer.Tests.TransferTestSupport;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// 传输设置（限速 / 切断）的端点测试：请求校验与"400 带原因"，路径匹配的边界，以及切断的确切语义。
/// 基本行为见 TransferTests，这里守它们守不住的地方。
/// </summary>
public sealed class TransferEndpointTests(MockServerFixture fixture) : IClassFixture<MockServerFixture>
{
    private async Task<byte[]> ArrangePackageAsync(string name, int size, CancellationToken cancellationToken)
    {
        await fixture.ResetAsync(cancellationToken);
        byte[] package = MakePackage(size);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", name), package);
        return package;
    }

    private async Task ArmAsync(string body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage armed = await fixture.Client.PostAsync("_control/transfer", Json(body), cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, armed.StatusCode);
    }

    private async Task<(byte[] Received, Exception? Failure)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await fixture.Client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadBodyAsync(response, cancellationToken);
    }

    // ---- 请求校验：一律 400 + 一句纯文本原因 ----

    [Theory]
    [InlineData("""{"path":"/x"}""", "至少")]
    [InlineData("""{"path":"/x","bytesPerSecond":null,"cutAfterBytes":null}""", "至少")]
    [InlineData("[]", "JSON 对象")]
    [InlineData("null", "JSON 对象")]
    [InlineData("\"a string\"", "JSON 对象")]
    [InlineData("", "JSON")]
    [InlineData("""{"path":""", "JSON")]
    [InlineData("""{"path":"/x","cutAfterBytes":0}""", "cutAfterBytes")]
    [InlineData("""{"path":"/x","bytesPerSecond":-1}""", "bytesPerSecond")]
    [InlineData("""{"path":"/x","bytesPerSecond":1.5}""", "bytesPerSecond 必须是整数")]
    [InlineData("""{"path":"/x","bytesPerSecond":"100"}""", "bytesPerSecond 必须是整数")]
    [InlineData("""{"path":"/x","bytesPerSecond":2147483648}""", "bytesPerSecond 必须是整数")]
    [InlineData("""{"path":"/x","cutAfterBytes":"5"}""", "cutAfterBytes 必须是整数")]
    [InlineData("""{"path":"/x","cutAfterBytes":1.5}""", "cutAfterBytes 必须是整数")]
    [InlineData("""{"path":"/x","cutAfterBytes":9223372036854775808}""", "cutAfterBytes 必须是整数")]
    [InlineData("""{"path":5,"cutAfterBytes":1}""", "path 必须是字符串")]
    [InlineData("""{"path":"","cutAfterBytes":1}""", "path")]
    [InlineData("""{"path":"   ","cutAfterBytes":1}""", "path")]
    [InlineData("""{"path":null,"cutAfterBytes":1}""", "path")]
    [InlineData("""{"cutAfterBytes":1}""", "path")]
    [InlineData("""{"path":"/x","cutAfterBytes":1,"cutafterbytes":2}""", "未知字段")]
    [InlineData("""{"path":"/x","cutAfterBytes":1,"extra":true}""", "未知字段")]
    [InlineData("""{"path":"/a","path":"/b","cutAfterBytes":1}""", "重复")]
    [InlineData("""{"path":"/x","cutAfterBytes":1,"cutAfterBytes":2}""", "重复")]
    public async Task Rejections_are_400_with_a_plain_text_reason(string body, string reasonFragment)
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.PostAsync("_control/transfer", Json(body), TestContext.Current.CancellationToken);

        // 重复的属性名也必须是 400，不能变成 500。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string reason = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(reasonFragment, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"path":"/x","bytesPerSecond":0}""")]
    [InlineData("""{"path":"/x","bytesPerSecond":2147483647}""")]
    [InlineData("""{"path":"/x","bytesPerSecond":1,"cutAfterBytes":1}""")]
    [InlineData("""{"path":"/x","cutAfterBytes":9223372036854775807}""")]
    [InlineData("""{"path":"/x","bytesPerSecond":100,"cutAfterBytes":null}""")]
    public async Task Boundary_values_are_accepted(string body)
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response =
            await fixture.Client.PostAsync("_control/transfer", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- 路径匹配：精确、大小写不敏感、不看查询串 ----

    [Fact]
    public async Task A_path_without_a_leading_slash_is_normalised()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await ArrangePackageAsync("noslash.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"packages/noslash.zip","cutAfterBytes":2048}""", cancellationToken);

        (byte[] received, Exception? failure) = await DownloadAsync("packages/noslash.zip", cancellationToken);

        Assert.Equal(2048, received.Length);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Path_matching_ignores_case()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await ArrangePackageAsync("case.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"/PACKAGES/Case.ZIP","cutAfterBytes":2048}""", cancellationToken);

        (byte[] received, Exception? failure) = await DownloadAsync("packages/case.zip", cancellationToken);

        Assert.Equal(2048, received.Length);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task The_query_string_does_not_change_the_match()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await ArrangePackageAsync("query.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/query.zip","cutAfterBytes":2048}""", cancellationToken);

        (byte[] received, Exception? failure) = await DownloadAsync("packages/query.zip?attempt=2", cancellationToken);

        Assert.Equal(2048, received.Length);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task A_longer_path_that_merely_starts_with_the_armed_path_is_left_alone()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("prefix.zip", 100_000, cancellationToken);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "prefix.zip.sig"), package);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "xprefix.zip"), package);
        await ArmAsync("""{"path":"/packages/prefix.zip","cutAfterBytes":1024}""", cancellationToken);

        byte[] longer = await fixture.Client.GetByteArrayAsync("packages/prefix.zip.sig", cancellationToken);
        byte[] shifted = await fixture.Client.GetByteArrayAsync("packages/xprefix.zip", cancellationToken);

        Assert.Equal(package, longer);
        Assert.Equal(package, shifted);
    }

    [Fact]
    public async Task Two_paths_can_be_armed_at_once_and_each_keeps_its_own_setting()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("two-a.zip", 100_000, cancellationToken);
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", "two-b.zip"), package);
        await ArmAsync("""{"path":"/packages/two-a.zip","cutAfterBytes":1000}""", cancellationToken);
        await ArmAsync("""{"path":"/packages/two-b.zip","cutAfterBytes":3000}""", cancellationToken);

        (byte[] a, _) = await DownloadAsync("packages/two-a.zip", cancellationToken);
        (byte[] b, _) = await DownloadAsync("packages/two-b.zip", cancellationToken);

        Assert.Equal(1000, a.Length);
        Assert.Equal(3000, b.Length);
    }

    [Fact]
    public async Task Arming_a_path_again_replaces_the_setting_instead_of_merging()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("replace.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/replace.zip","cutAfterBytes":1000}""", cancellationToken);

        // 第二次只限速、不切断：旧的切断不能被悄悄留下。
        await ArmAsync("""{"path":"/packages/replace.zip","bytesPerSecond":50000000}""", cancellationToken);

        byte[] downloaded = await fixture.Client.GetByteArrayAsync("packages/replace.zip", cancellationToken);
        Assert.Equal(package, downloaded);
    }

    [Fact]
    public async Task A_zero_rate_on_its_own_is_accepted_and_means_no_transfer_effect()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("zero.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/zero.zip","cutAfterBytes":1000}""", cancellationToken);

        // 0 = 不限速；用它盖掉同一路径上的切断，就是"只撤这一条"的办法（DELETE 是清全部）。
        await ArmAsync("""{"path":"/packages/zero.zip","bytesPerSecond":0}""", cancellationToken);

        byte[] downloaded = await fixture.Client.GetByteArrayAsync("packages/zero.zip", cancellationToken);
        Assert.Equal(package, downloaded);
    }

    // ---- 切断的确切语义 ----

    [Fact]
    public async Task A_limit_at_or_beyond_the_body_length_delivers_everything_without_an_error()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("whole.zip", 100_000, cancellationToken);

        await ArmAsync("""{"path":"/packages/whole.zip","cutAfterBytes":100000}""", cancellationToken);
        byte[] exact = await fixture.Client.GetByteArrayAsync("packages/whole.zip", cancellationToken);

        await ArmAsync("""{"path":"/packages/whole.zip","cutAfterBytes":5000000}""", cancellationToken);
        byte[] beyond = await fixture.Client.GetByteArrayAsync("packages/whole.zip", cancellationToken);

        Assert.Equal(package, exact);
        Assert.Equal(package, beyond);
    }

    [Fact]
    public async Task The_limit_counts_the_bytes_of_the_response_not_of_the_file()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("ranged.zip", 200_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/ranged.zip","cutAfterBytes":10000}""", cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "packages/ranged.zip");
        request.Headers.Range = new RangeHeaderValue(50_000, null);
        using HttpResponseMessage response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        (byte[] received, Exception? failure) = await ReadBodyAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(package[50_000..60_000], received);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Throttle_and_cut_together_still_deliver_an_exact_prefix()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("both.zip", 200_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/both.zip","bytesPerSecond":2000000,"cutAfterBytes":20000}""", cancellationToken);

        (byte[] received, Exception? failure) = await DownloadAsync("packages/both.zip", cancellationToken);

        Assert.Equal(package[..20_000], received);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task A_head_request_on_an_armed_path_is_answered_normally()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await ArrangePackageAsync("head.zip", 100_000, cancellationToken);
        await ArmAsync("""{"path":"/packages/head.zip","bytesPerSecond":100,"cutAfterBytes":1}""", cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Head, "packages/head.zip");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100_000, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task The_cut_ends_the_body_cleanly_so_every_flushed_byte_arrives()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] package = await ArrangePackageAsync("clean.zip", 300_000, cancellationToken);

        // 切在离文件末尾只差一点的位置：留在 Kestrel 管道里没发出去的字节最多，Abort 最容易丢它们。
        await ArmAsync("""{"path":"/packages/clean.zip","cutAfterBytes":299999}""", cancellationToken);

        (byte[] received, Exception? failure) = await DownloadAsync("packages/clean.zip", cancellationToken);

        // 连接被 Abort 时客户端得到 SocketException（连接被重置）并且丢字节；干净收尾时得到 HttpIOException（响应提前结束）。
        // 断点续传要的是后者：到手的字节数必须恰好等于 cutAfterBytes，客户端才能可靠地从那里续传。
        HttpIOException ended = Assert.IsType<HttpIOException>(failure);
        Assert.Equal(HttpRequestError.ResponseEnded, ended.HttpRequestError);
        Assert.Equal(package[..299_999], received);
    }
}
