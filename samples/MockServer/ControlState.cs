namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 服务器的全部可变状态：上报、故障、feed 叠加、传输。
/// 每一块各占一个 partial 文件，所有成员线程安全。
/// </summary>
internal sealed partial class ControlState(string root, OverlayFileProvider files)
{
    /// <summary>静态托管的根目录，控制面据此读取磁盘上的 releases.json 作为叠加底稿。</summary>
    public string Root { get; } = root;

    /// <summary>feed 的内存叠加入口。控制面往这里写，磁盘一字不改。</summary>
    public OverlayFileProvider Files { get; } = files;

    /// <summary>撤销 feed 叠加，请求随即回到磁盘内容。时间戳计数器（见 ControlState.Overlay.cs）刻意不归零。</summary>
    public void ClearOverlay()
    {
        Files.Remove(FeedUrlPath);
    }

    /// <summary>清空全部传输设置（限速与中途断流）。存储在 ControlState.Transfer.cs。</summary>
    public void ClearTransfers()
    {
        lock (_transferGate)
        {
            _transfers.Clear();
        }
    }
}
