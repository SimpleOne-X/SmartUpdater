using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal static partial class ExpectedPublicApi
{
    /// <summary>三个抽象、两个数据类型与四个事件参数。</summary>
    public static readonly string[] Abstractions =
    [
        nameof(IReleaseFeed),
        nameof(FeedResult),
        nameof(IPackageDownloader),
        nameof(DownloadProgress),
        nameof(IUpdateReporter),
        nameof(UpdateAvailableEventArgs),
        nameof(UpdateProgressEventArgs),
        nameof(RestartingEventArgs),
        nameof(UpdateFailedEventArgs),
    ];
}
