using Microsoft.Extensions.FileProviders;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// 直接测 <see cref="ControlState.NextOverlayStamp"/> 的单调性。HTTP 层的 ETag 测试受"两次请求是否落在同一秒"影响，
/// 这里不受影响：ETag = LastModified（截到秒）^ Length，只要每次相差至少一整秒，同长度的内容就绝不会撞 ETag。
/// </summary>
public sealed class OverlayStampTests
{
    private static ControlState NewState()
        => new("unused", new OverlayFileProvider(new NullFileProvider()));

    [Fact]
    public void Consecutive_stamps_are_at_least_one_second_apart()
    {
        ControlState state = NewState();

        DateTimeOffset previous = state.NextOverlayStamp();
        for (int i = 0; i < 5; i++)
        {
            DateTimeOffset next = state.NextOverlayStamp();
            Assert.True(next - previous >= TimeSpan.FromSeconds(1), $"{previous:O} -> {next:O}");
            previous = next;
        }
    }

    [Fact]
    public void Stamps_are_never_behind_the_wall_clock()
    {
        ControlState state = NewState();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        Assert.True(state.NextOverlayStamp() >= before);
    }

    [Fact]
    public void Stamps_keep_advancing_across_clear_overlay()
    {
        // 撤销后再叠加同长度的内容，不能撞上撤销前客户端缓存的那个 ETag，所以计数器不能随撤销归零。
        ControlState state = NewState();
        DateTimeOffset before = state.NextOverlayStamp();

        state.ClearOverlay();
        DateTimeOffset after = state.NextOverlayStamp();

        Assert.True(after - before >= TimeSpan.FromSeconds(1), $"{before:O} -> {after:O}");
    }

    [Fact]
    public void Concurrent_callers_never_share_a_stamp_second()
    {
        ControlState state = NewState();
        DateTimeOffset[] stamps = new DateTimeOffset[20_000];

        Parallel.For(0, stamps.Length, i => stamps[i] = state.NextOverlayStamp());

        DateTimeOffset[] sorted = [.. stamps.Order()];
        for (int i = 1; i < sorted.Length; i++)
        {
            Assert.True(sorted[i] - sorted[i - 1] >= TimeSpan.FromSeconds(1), $"{sorted[i - 1]:O} -> {sorted[i]:O}");
        }
    }

    [Fact]
    public void Clear_overlay_removes_only_the_feed_overlay_and_is_safe_to_repeat()
    {
        var files = new OverlayFileProvider(new NullFileProvider());
        var state = new ControlState("unused", files);
        files.Set(ControlState.FeedUrlPath, [1, 2, 3], DateTimeOffset.UtcNow);

        state.ClearOverlay();
        state.ClearOverlay();

        Assert.False(files.GetFileInfo(ControlState.FeedUrlPath).Exists);
    }
}
