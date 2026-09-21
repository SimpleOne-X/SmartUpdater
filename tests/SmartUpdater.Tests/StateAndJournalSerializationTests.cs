using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class StateAndJournalSerializationTests
{
    // 全局注册的 VersionJsonConverter 是否生效，只有错误消息能区分（BCL 自带的 System.Version 转换器措辞不同）。
    [Fact]
    public void Malformed_version_string_is_rejected_with_the_package_converter_message()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("""{"skippedVersions":["bad"]}""", SmartUpdaterJsonContext.Default.UpdateState));

        Assert.Contains("不是合法的版本号", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void State_round_trips_all_fields()
    {
        var state = new UpdateState
        {
            CurrentVersion = new Version(1, 2, 3),
            DeviceGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            SkippedVersions = [new Version(1, 2, 4), new Version(2, 0)],
            FeedETag = "\"abc\"",
            LastCheckedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
            LastReportedAt = null,
        };

        string json = JsonSerializer.Serialize(state, SmartUpdaterJsonContext.Default.UpdateState);

        Assert.Contains("\"currentVersion\":\"1.2.3\"", json);
        Assert.Contains("\"skippedVersions\":[\"1.2.4\",\"2.0\"]", json);
        Assert.DoesNotContain("\"lastReportedAt\"", json);

        UpdateState back = JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateState)!;

        Assert.Equal(new Version(1, 2, 3), back.CurrentVersion);
        Assert.Equal(state.DeviceGuid, back.DeviceGuid);
        Assert.Equal(new[] { new Version(1, 2, 4), new Version(2, 0) }, back.SkippedVersions);
        Assert.Equal("\"abc\"", back.FeedETag);
        Assert.Equal(state.LastCheckedAt, back.LastCheckedAt);
        Assert.Null(back.LastReportedAt);
    }

    [Fact]
    public void Empty_state_document_yields_defaults()
    {
        UpdateState state = JsonSerializer.Deserialize("{}", SmartUpdaterJsonContext.Default.UpdateState)!;

        Assert.Null(state.CurrentVersion);
        Assert.Equal(Guid.Empty, state.DeviceGuid);
        Assert.NotNull(state.SkippedVersions);
        Assert.Empty(state.SkippedVersions);
        Assert.Null(state.FeedETag);
        Assert.Null(state.LastCheckedAt);
    }

    [Fact]
    public void Journal_round_trips_and_writes_state_in_camel_case()
    {
        var journal = new UpdateJournal
        {
            State = JournalState.Committing,
            FromVersion = new Version(1, 0, 0),
            ToVersion = new Version(1, 1, 0),
            StartedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
            Writes =
            [
                new JournalEntry { Path = "app.exe", HadTarget = true },
                new JournalEntry { Path = "new.dll", HadTarget = false },
            ],
            Deletes = ["old.dll"],
        };

        string json = JsonSerializer.Serialize(journal, SmartUpdaterJsonContext.Default.UpdateJournal);

        Assert.Contains("\"state\":\"committing\"", json);
        Assert.Contains("\"hadTarget\":false", json);

        UpdateJournal back = JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateJournal)!;

        Assert.Equal(JournalState.Committing, back.State);
        Assert.Equal(new Version(1, 0, 0), back.FromVersion);
        Assert.Equal(new Version(1, 1, 0), back.ToVersion);
        Assert.Equal(journal.StartedAt, back.StartedAt);
        Assert.Equal(2, back.Writes.Count);
        Assert.Equal("new.dll", back.Writes[1].Path);
        Assert.False(back.Writes[1].HadTarget);
        Assert.True(back.Writes[0].HadTarget);
        Assert.Equal(new[] { "old.dll" }, back.Deletes);
    }

    // JournalState 是 internal 枚举：public 测试方法的参数不能是 internal 类型（CS0051），所以用成员名传，体内再解析。
    [Theory]
    [InlineData("Preparing", "preparing")]
    [InlineData("Committing", "committing")]
    [InlineData("Done", "done")]
    public void Journal_state_literals_match_decision_2(string stateName, string literal)
    {
        var journal = new UpdateJournal { State = Enum.Parse<JournalState>(stateName), ToVersion = new Version(1, 0, 0) };

        string json = JsonSerializer.Serialize(journal, SmartUpdaterJsonContext.Default.UpdateJournal);

        Assert.Contains($"\"state\":\"{literal}\"", json);
    }

    [Fact]
    public void First_install_journal_has_no_from_version()
    {
        var journal = new UpdateJournal { State = JournalState.Preparing, FromVersion = null, ToVersion = new Version(1, 0, 0) };

        string json = JsonSerializer.Serialize(journal, SmartUpdaterJsonContext.Default.UpdateJournal);
        UpdateJournal back = JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateJournal)!;

        Assert.DoesNotContain("\"fromVersion\"", json);
        Assert.Null(back.FromVersion);
        Assert.Empty(back.Writes);
        Assert.Empty(back.Deletes);
    }

    [Theory]
    [InlineData("""{"state":"bogus","toVersion":"1.0.0"}""")]
    [InlineData("""{"state":"done","toVersion":"not-a-version"}""")]
    [InlineData("""{"state":"done","toVersion":"1.0.0","writes":[{"path":"a"}""")]
    [InlineData("")]
    public void Corrupt_journal_json_throws_JsonException(string json)
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(json, SmartUpdaterJsonContext.Default.UpdateJournal));
    }
}
