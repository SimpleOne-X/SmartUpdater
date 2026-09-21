namespace SimpleOneX.SmartUpdater;

/// <summary>取回 feed 的服务。数据类型叫 <see cref="ReleaseFeedDocument"/>，两者不能同名。</summary>
public interface IReleaseFeed
{
    /// <summary>
    /// <paramref name="etag"/> 为上次拿到的值（首次为 null）。
    /// 内容未变更时返回 <see cref="FeedResult.NotModified"/>，包据此跳过解析。
    /// </summary>
    Task<FeedResult> GetAsync(string? etag, CancellationToken ct);
}

/// <summary><see cref="IReleaseFeed.GetAsync"/> 的结果。</summary>
/// <param name="IsNotModified">内容自上次 ETag 以来未变更。</param>
/// <param name="Document">解析出的文档；<paramref name="IsNotModified"/> 为 true 时为 null。</param>
/// <param name="ETag">本次拿到的 ETag，供下次请求使用；服务器未提供时为 null。</param>
public sealed record FeedResult(bool IsNotModified, ReleaseFeedDocument? Document, string? ETag)
{
    /// <summary>内容未变更。</summary>
    public static FeedResult NotModified { get; } = new(true, null, null);
}
