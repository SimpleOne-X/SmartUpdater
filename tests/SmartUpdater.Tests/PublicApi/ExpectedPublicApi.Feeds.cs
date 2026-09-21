using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal static partial class ExpectedPublicApi
{
    /// <summary>两个 feed 实现。</summary>
    public static readonly string[] Feeds = [nameof(HttpReleaseFeed), nameof(FileReleaseFeed)];
}
