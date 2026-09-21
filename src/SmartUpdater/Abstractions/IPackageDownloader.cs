namespace SimpleOneX.SmartUpdater;

/// <summary>按 <c>package.url</c> 下载升级包。</summary>
public interface IPackageDownloader
{
    /// <summary>把 <paramref name="release"/> 的包下载到 <paramref name="destinationPath"/>，返回最终文件路径。</summary>
    Task<string> DownloadAsync(ReleaseEntry release, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct);
}

/// <summary>下载进度。</summary>
/// <param name="BytesReceived">已收到的字节数（含续传前已有的部分）。</param>
/// <param name="TotalBytes">包的总字节数。</param>
/// <param name="BytesPerSecond">当前速度。</param>
public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, double BytesPerSecond)
{
    /// <summary>百分比 0~100；总大小未知时为 0。</summary>
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100);
}
