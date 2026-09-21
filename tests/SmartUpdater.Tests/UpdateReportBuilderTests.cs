using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateReportBuilderTests
{
    private static readonly Guid Device = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly UpdateEnvironment Env = new() { MachineName = "PC-01", OsVersion = "Microsoft Windows NT 10.0.26200.0" };

    [Fact]
    public void Updated_report_fields()
    {
        UpdateReport r = UpdateReportBuilder.Updated(Device, Env, new Version(1, 2, 3, 0), new Version(1, 2, 4, 0), 123, Now);

        Assert.Equal(UpdateEventType.Updated, r.EventType);
        Assert.Equal(Device, r.DeviceGuid);
        Assert.Equal("PC-01", r.MachineName);
        Assert.Null(r.Stage);
        Assert.Equal(new Version(1, 2, 3, 0), r.FromVersion);
        Assert.Equal(new Version(1, 2, 4, 0), r.ToVersion);
        Assert.True(r.IsSuccess);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(0, r.DurationMs);
        Assert.Equal(123, r.DiskFreeBytes);
        Assert.Equal("Microsoft Windows NT 10.0.26200.0", r.OsVersion);
        Assert.Equal(Now, r.ReportedAt);
        Assert.Null(r.LogTail);
    }

    [Fact]
    public void Failed_report_fields()
    {
        UpdateReport r = UpdateReportBuilder.Failed(Device, Env, UpdateStage.Download, "boom", new Version(1, 2, 3, 0), new Version(1, 2, 4, 0), 4567, null, "tail", Now);

        Assert.Equal(UpdateEventType.Failed, r.EventType);
        Assert.Equal(UpdateStage.Download, r.Stage);
        Assert.False(r.IsSuccess);
        Assert.Equal("boom", r.ErrorMessage);
        Assert.Equal(4567, r.DurationMs);
        Assert.Null(r.DiskFreeBytes);
        Assert.Equal("tail", r.LogTail);
        Assert.Equal(new Version(1, 2, 4, 0), r.ToVersion);
    }

    [Fact]
    public void Failed_report_truncates_oversized_log_tail_keeping_the_end_and_nulls_empty_tail()
    {
        string tail = string.Concat(Enumerable.Repeat("0123456789\n", 1000));    // 11 000 字节

        UpdateReport r = UpdateReportBuilder.Failed(Device, Env, UpdateStage.Commit, "x", null, null, 0, null, tail, Now);
        UpdateReport empty = UpdateReportBuilder.Failed(Device, Env, UpdateStage.Commit, "x", null, null, 0, null, "", Now);

        Assert.True(Encoding.UTF8.GetByteCount(r.LogTail!) <= UpdateReportBuilder.MaxLogTailBytes);
        Assert.EndsWith("0123456789\n", r.LogTail, StringComparison.Ordinal);
        Assert.Null(empty.LogTail);
    }

    [Fact]
    public void Heartbeat_report_fields()
    {
        UpdateReport r = UpdateReportBuilder.Heartbeat(Device, Env, new Version(1, 2, 3, 0), 5, Now);

        Assert.Equal(UpdateEventType.Heartbeat, r.EventType);
        Assert.True(r.IsSuccess);
        Assert.Null(r.Stage);
        Assert.Null(r.FromVersion);
        Assert.Equal(new Version(1, 2, 3, 0), r.ToVersion);
        Assert.Equal(5, r.DiskFreeBytes);
        Assert.Null(r.LogTail);
    }
}
