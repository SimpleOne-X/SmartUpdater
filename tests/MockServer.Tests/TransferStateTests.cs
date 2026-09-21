using Microsoft.Extensions.FileProviders;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>ControlState 传输部分的单元测试：不经 HTTP，直接打存取接口。</summary>
public sealed class TransferStateTests
{
    private static ControlState CreateState()
        => new(Path.GetTempPath(), new OverlayFileProvider(new NullFileProvider()));

    [Fact]
    public void An_unknown_path_has_no_transfer_setting()
    {
        ControlState state = CreateState();

        Assert.False(state.TryGetTransfer("/packages/none.zip", out int bytesPerSecond, out long? cutAfterBytes));
        Assert.Equal(0, bytesPerSecond);
        Assert.Null(cutAfterBytes);
    }

    [Fact]
    public void A_missing_rate_is_stored_as_zero_and_a_missing_cut_stays_null()
    {
        ControlState state = CreateState();
        state.SetTransfer(new TransferRequest("/a", null, 10));
        state.SetTransfer(new TransferRequest("/b", 500, null));

        Assert.True(state.TryGetTransfer("/a", out int aRate, out long? aCut));
        Assert.Equal((0, 10L), (aRate, aCut));
        Assert.True(state.TryGetTransfer("/b", out int bRate, out long? bCut));
        Assert.Equal((500, (long?)null), (bRate, bCut));
    }

    [Theory]
    [InlineData("/packages/a.zip", "/packages/a.zip")]
    [InlineData("packages/a.zip", "/packages/a.zip")]
    [InlineData("/packages/a.zip", "packages/a.zip")]
    [InlineData("/Packages/A.ZIP", "/packages/a.zip")]
    public void Paths_are_matched_without_regard_to_a_leading_slash_or_case(string armed, string requested)
    {
        ControlState state = CreateState();
        state.SetTransfer(new TransferRequest(armed, null, 10));

        Assert.True(state.TryGetTransfer(requested, out _, out long? cut));
        Assert.Equal(10, cut);
    }

    [Theory]
    [InlineData("/packages/a.zip.sig")]
    [InlineData("/packages/a.zi")]
    [InlineData("/x/packages/a.zip")]
    [InlineData("/packages/a.zip/")]
    public void Only_the_exact_path_matches(string requested)
    {
        ControlState state = CreateState();
        state.SetTransfer(new TransferRequest("/packages/a.zip", null, 10));

        Assert.False(state.TryGetTransfer(requested, out _, out _));
    }

    [Fact]
    public void Clear_removes_every_setting()
    {
        ControlState state = CreateState();
        state.SetTransfer(new TransferRequest("/a", 1, null));
        state.SetTransfer(new TransferRequest("/b", null, 2));

        state.ClearTransfers();

        Assert.False(state.TryGetTransfer("/a", out _, out _));
        Assert.False(state.TryGetTransfer("/b", out _, out _));
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_never_corrupt_the_store()
    {
        ControlState state = CreateState();
        const int Writers = 8;
        const int PerWriter = 2_000;

        Task[] tasks =
        [
            .. Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                {
                    string path = $"/w{writer}/{i}";
                    state.SetTransfer(new TransferRequest(path, writer + 1, i + 1));

                    // 自己刚写的一定读得回；同时别的线程在写别的路径、读不存在的路径。
                    Assert.True(state.TryGetTransfer(path, out int rate, out long? cut));
                    Assert.Equal((writer + 1, (long?)(i + 1)), (rate, cut));
                    Assert.False(state.TryGetTransfer($"/missing/{writer}/{i}", out _, out _));
                }
            })),
        ];

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        for (int writer = 0; writer < Writers; writer++)
        {
            for (int i = 0; i < PerWriter; i++)
            {
                Assert.True(state.TryGetTransfer($"/w{writer}/{i}", out int rate, out long? cut));
                Assert.Equal((writer + 1, (long?)(i + 1)), (rate, cut));
            }
        }
    }
}
