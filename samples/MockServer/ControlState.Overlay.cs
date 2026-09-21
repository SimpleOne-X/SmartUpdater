namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>feed 叠加部分：为每一次叠加发放严格递增的 LastModified。ClearOverlay 在 ControlState.cs 主文件里。</summary>
internal sealed partial class ControlState
{
    /// <summary>被叠加的那一个静态路径：只允许改 feed。</summary>
    public const string FeedUrlPath = "/releases.json";

    private long _lastOverlayTicks;
    private readonly Lock _overlayGate = new();

    /// <summary>
    /// 每次叠加都要一个严格递增、且比上次至少大 1 秒的时间戳。
    /// 静态文件中间件的 ETag = LastModified（截到秒）.ToFileTime() ^ Length：
    /// 若长度相同而时间戳不变，两份不同的内容会得到同一个 ETag，
    /// 客户端拿旧 ETag 回来就一直是 304，永远看不到新下发的 rolloutPercent。
    /// 加满 1 秒同时绕开 Last-Modified 响应头的秒级粒度。
    /// 计数器不随 ClearOverlay / reset 归零：撤销后再叠加同长度的内容，仍不能撞上撤销前客户端缓存的 ETag。
    /// </summary>
    public DateTimeOffset NextOverlayStamp()
    {
        lock (_overlayGate)
        {
            long now = DateTimeOffset.UtcNow.UtcTicks;
            long next = Math.Max(now, _lastOverlayTicks + TimeSpan.TicksPerSecond);
            _lastOverlayTicks = next;
            return new DateTimeOffset(next, TimeSpan.Zero);
        }
    }
}
