using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateReportSerializationTests
{
    private static UpdateReport FailedReport() => new()
    {
        EventType = UpdateEventType.Failed,
        DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        MachineName = "PC-01",
        Stage = UpdateStage.Commit,
        FromVersion = new Version(1, 2, 3),
        ToVersion = new Version(1, 2, 4),
        IsSuccess = false,
        ErrorMessage = "boom",
        DurationMs = 1234,
        DiskFreeBytes = 5_000_000_000,
        OsVersion = "Microsoft Windows NT 10.0.26200.0",
        ReportedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        LogTail = "last lines",
    };

    [Fact]
    public void Failed_report_writes_every_decision_14_field_in_camel_case()
    {
        string json = JsonSerializer.Serialize(FailedReport(), SmartUpdaterJsonContext.Default.UpdateReport);

        Assert.Contains("\"eventType\":\"Failed\"", json);
        Assert.Contains("\"deviceGuid\":\"11111111-2222-3333-4444-555555555555\"", json);
        Assert.Contains("\"machineName\":\"PC-01\"", json);
        Assert.Contains("\"stage\":\"Commit\"", json);
        Assert.Contains("\"fromVersion\":\"1.2.3\"", json);
        Assert.Contains("\"toVersion\":\"1.2.4\"", json);
        Assert.Contains("\"isSuccess\":false", json);
        Assert.Contains("\"errorMessage\":\"boom\"", json);
        Assert.Contains("\"durationMs\":1234", json);
        Assert.Contains("\"diskFreeBytes\":5000000000", json);
        Assert.Contains("\"osVersion\":\"Microsoft Windows NT 10.0.26200.0\"", json);
        Assert.Contains("\"reportedAt\":\"2026-09-18T10:00:00+00:00\"", json);
        Assert.Contains("\"logTail\":\"last lines\"", json);
    }

    [Fact]
    public void Heartbeat_report_omits_null_fields()
    {
        var report = new UpdateReport
        {
            EventType = UpdateEventType.Heartbeat,
            DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            IsSuccess = true,
            ReportedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        };

        string json = JsonSerializer.Serialize(report, SmartUpdaterJsonContext.Default.UpdateReport);

        Assert.Contains("\"eventType\":\"Heartbeat\"", json);
        Assert.Contains("\"isSuccess\":true", json);
        Assert.DoesNotContain("\"stage\"", json);
        Assert.DoesNotContain("\"fromVersion\"", json);
        Assert.DoesNotContain("\"toVersion\"", json);
        Assert.DoesNotContain("\"errorMessage\"", json);
        Assert.DoesNotContain("\"logTail\"", json);
        Assert.DoesNotContain("\"diskFreeBytes\"", json);
    }

    [Fact]
    public void Report_round_trips()
    {
        UpdateReport original = FailedReport();

        string json = JsonSerializer.Serialize(original, SmartUpdaterJsonContext.Default.UpdateReport);
        UpdateReport back = JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateReport)!;

        Assert.Equal(UpdateEventType.Failed, back.EventType);
        Assert.Equal(original.DeviceGuid, back.DeviceGuid);
        Assert.Equal("PC-01", back.MachineName);
        Assert.Equal(UpdateStage.Commit, back.Stage);
        Assert.Equal(new Version(1, 2, 3), back.FromVersion);
        Assert.Equal(new Version(1, 2, 4), back.ToVersion);
        Assert.False(back.IsSuccess);
        Assert.Equal("boom", back.ErrorMessage);
        Assert.Equal(1234, back.DurationMs);
        Assert.Equal(5_000_000_000, back.DiskFreeBytes);
        Assert.Equal("Microsoft Windows NT 10.0.26200.0", back.OsVersion);
        Assert.Equal(original.ReportedAt, back.ReportedAt);
        Assert.Equal("last lines", back.LogTail);
    }

    [Fact]
    public void Unknown_event_type_throws_JsonException()
    {
        const string json = """
        {"eventType":"Exploded","deviceGuid":"11111111-2222-3333-4444-555555555555","reportedAt":"2026-09-18T10:00:00+00:00"}
        """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateReport));
    }

    [Fact]
    public void UpdateStage_members_follow_the_pipeline_order_of_decision_4()
    {
        Assert.Equal(
            new[] { "Check", "Download", "Verify", "DiskCheck", "PermissionCheck", "Commit", "Rollback" },
            Enum.GetNames<UpdateStage>());
    }

    [Fact]
    public void UpdateEventType_members_match_section_4_1()
    {
        Assert.Equal(new[] { "Updated", "Failed", "Heartbeat" }, Enum.GetNames<UpdateEventType>());
    }

    [Fact]
    public void UpdateLogLevel_is_ordered_for_threshold_filtering()
    {
        Assert.Equal(new[] { "Debug", "Information", "Warning", "Error" }, Enum.GetNames<UpdateLogLevel>());
        Assert.True(UpdateLogLevel.Debug < UpdateLogLevel.Information);
        Assert.True(UpdateLogLevel.Information < UpdateLogLevel.Warning);
        Assert.True(UpdateLogLevel.Warning < UpdateLogLevel.Error);
    }
}
