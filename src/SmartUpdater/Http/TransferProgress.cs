namespace SimpleOneX.SmartUpdater;

/// <summary>把「已收字节」累计成 <see cref="DownloadProgress"/> 并报告。速度 = 本次会话累计字节 / 自构造以来的秒数。</summary>
internal sealed class TransferProgress
{
    private readonly long _totalBytes;
    private readonly TimeProvider _timeProvider;
    private readonly IProgress<DownloadProgress>? _sink;
    private readonly long _start;
    private long _sessionBytes;

    /// <summary>创建进度累计器。</summary>
    /// <param name="totalBytes">包的总字节数。</param>
    /// <param name="alreadyReceived">续传前已有的字节数。</param>
    /// <param name="timeProvider">计时用的时钟。</param>
    /// <param name="sink">报告目标，可为 null。</param>
    public TransferProgress(long totalBytes, long alreadyReceived, TimeProvider timeProvider, IProgress<DownloadProgress>? sink)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _totalBytes = totalBytes;
        _timeProvider = timeProvider;
        _sink = sink;
        BytesReceived = alreadyReceived;
        _start = timeProvider.GetTimestamp();
    }

    /// <summary>已收字节数（含续传前已有的部分）。</summary>
    public long BytesReceived { get; private set; }

    /// <summary>累加 <paramref name="bytes"/> 并报告一次。</summary>
    public void Add(int bytes)
    {
        BytesReceived += bytes;
        _sessionBytes += bytes;
        Report();
    }

    /// <summary>最后一次报告；<see cref="BytesReceived"/> 不变。</summary>
    public void Complete() => Report();

    private void Report()
    {
        double seconds = _timeProvider.GetElapsedTime(_start).TotalSeconds;
        double speed = seconds > 0 ? _sessionBytes / seconds : 0;
        _sink?.Report(new DownloadProgress(BytesReceived, _totalBytes, speed));
    }
}
