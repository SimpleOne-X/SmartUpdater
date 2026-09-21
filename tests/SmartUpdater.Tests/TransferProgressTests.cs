using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class TransferProgressTests
{
    private sealed class Collector : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public void Reports_cumulative_bytes_and_speed_from_the_injected_clock()
    {
        var time = new ManualTimeProvider();
        var sink = new Collector();
        var progress = new TransferProgress(totalBytes: 4000, alreadyReceived: 1000, time, sink);

        progress.Add(1000);                       // 0 秒 → 速度 0
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Add(1000);                       // 本会话 2000 字节 / 1 秒
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Add(1000);                       // 3000 / 2 秒
        progress.Complete();

        Assert.Equal([2000, 3000, 4000, 4000], sink.Reports.Select(r => r.BytesReceived));
        Assert.Equal([0, 2000, 1500, 1500], sink.Reports.Select(r => r.BytesPerSecond));
        Assert.All(sink.Reports, r => Assert.Equal(4000, r.TotalBytes));
        Assert.Equal(100, sink.Reports[^1].Percent);
        Assert.Equal(4000, progress.BytesReceived);
    }

    [Fact]
    public void Null_sink_is_allowed()
    {
        var progress = new TransferProgress(10, 0, new ManualTimeProvider(), null);

        progress.Add(5);
        progress.Complete();

        Assert.Equal(5, progress.BytesReceived);
    }
}
