using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseFeedValidatorTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static ReleaseEntry Entry(
        string version,
        string url = "p.zip",
        long size = 10,
        string sha256 = Sha,
        string? minUpdatableFrom = null,
        int rolloutPercent = 100,
        DateTimeOffset? releasedAt = null)
        => new()
        {
            Version = Version.Parse(version),
            ReleasedAt = releasedAt ?? new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
            Package = new PackageInfo { Url = url, Size = size, Sha256 = sha256 },
            MinUpdatableFrom = minUpdatableFrom is null ? null : Version.Parse(minUpdatableFrom),
            RolloutPercent = rolloutPercent,
            Mode = UpdateMode.Optional,
            Notes = "n",
            Signature = "s",
        };

    private static ReleaseFeedDocument Doc(params ReleaseEntry[] releases)
        => new() { SchemaVersion = 1, Channel = "stable", Client = new ClientPolicy { PollIntervalSeconds = 120 }, Releases = releases };

    [Fact]
    public void Valid_document_is_returned_sorted_descending_with_canonical_versions_and_other_fields_intact()
    {
        var log = new RecordingLog();
        ReleaseFeedDocument doc = Doc(Entry("1.2.2", minUpdatableFrom: "1.0"), Entry("1.2.4"), Entry("1.2.3"));

        ValidatedFeed result = ReleaseFeedValidator.Validate(doc, log);

        Assert.Equal([new Version(1, 2, 4, 0), new Version(1, 2, 3, 0), new Version(1, 2, 2, 0)], result.Document.Releases.Select(r => r.Version));
        Assert.Equal(new Version(1, 0, 0, 0), result.Document.Releases[2].MinUpdatableFrom);
        Assert.Equal("stable", result.Document.Channel);
        Assert.Equal(120, result.Document.Client!.PollIntervalSeconds);
        Assert.Equal(1, result.Document.SchemaVersion);
        Assert.All(result.Document.Releases, r => Assert.Equal("n", r.Notes));
        Assert.All(result.Document.Releases, r => Assert.Equal("s", r.Signature));
        Assert.Empty(result.Document.ParseWarnings);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void RawOf_maps_normalized_entries_back_to_the_parsed_entries_for_signature_verification()
    {
        ReleaseEntry raw124 = Entry("1.2.4");
        ReleaseEntry raw122 = Entry("1.2.2", minUpdatableFrom: "1.0");

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(raw122, raw124), new RecordingLog());

        Assert.Same(raw124, result.RawOf(result.Document.Releases[0]));
        Assert.Same(raw122, result.RawOf(result.Document.Releases[1]));
        Assert.Equal(new Version(1, 2, 2), result.RawOf(result.Document.Releases[1]).Version);   // 原始写法保留
        Assert.Throws<ArgumentException>(() => result.RawOf(Entry("9.9")));
    }

    [Fact]
    public void Entries_that_are_already_canonical_are_kept_as_the_same_instances()
    {
        ReleaseEntry entry = Entry("1.2.4.0");

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(entry), new RecordingLog());

        ReleaseEntry only = Assert.Single(result.Document.Releases);
        Assert.Same(entry, only);
        Assert.Same(entry, result.RawOf(only));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Unknown_schema_version_is_rejected(int schemaVersion)
    {
        var doc = new ReleaseFeedDocument { SchemaVersion = schemaVersion, Releases = [Entry("1.2.4")] };

        var ex = Assert.Throws<FeedRejectedException>(() => ReleaseFeedValidator.Validate(doc, new RecordingLog()));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
        Assert.Contains(schemaVersion.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_versions_after_normalization_reject_the_whole_feed()
    {
        ReleaseFeedDocument doc = Doc(Entry("1.2.4"), Entry("1.2.3"), Entry("1.2.4.0"));

        var ex = Assert.Throws<FeedRejectedException>(() => ReleaseFeedValidator.Validate(doc, new RecordingLog()));

        Assert.Contains("1.2.4.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_warnings_are_logged_at_warning_level_with_check_stage()
    {
        var log = new RecordingLog();
        var doc = new ReleaseFeedDocument { SchemaVersion = 1, Releases = [Entry("1.0")], ParseWarnings = ["releases[1]: bad", "client: bad"] };

        ValidatedFeed result = ReleaseFeedValidator.Validate(doc, log);

        Assert.Equal(2, log.AtLevel(UpdateLogLevel.Warning).Count());
        Assert.All(log.Entries, e => Assert.Equal(UpdateStage.Check, e.Stage));
        Assert.True(log.Contains("releases[1]: bad"));
        Assert.Empty(result.Document.ParseWarnings);
    }

    [Theory]
    [InlineData("", 10, Sha, "url")]
    [InlineData("   ", 10, Sha, "url")]
    [InlineData("p.zip", 0, Sha, "size")]
    [InlineData("p.zip", -1, Sha, "size")]
    [InlineData("p.zip", 10, "abc", "sha256")]
    [InlineData("p.zip", 10, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85", "sha256")]
    [InlineData("p.zip", 10, "g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "sha256")]
    public void Entries_with_missing_or_invalid_required_fields_are_dropped_and_logged(string url, long size, string sha, string expectedWord)
    {
        var log = new RecordingLog();
        ReleaseFeedDocument doc = Doc(Entry("1.2.4", url: url, size: size, sha256: sha), Entry("1.2.3"));

        ValidatedFeed result = ReleaseFeedValidator.Validate(doc, log);

        Assert.Equal(new Version(1, 2, 3, 0), Assert.Single(result.Document.Releases).Version);
        LogEntry w = Assert.Single(log.AtLevel(UpdateLogLevel.Warning));
        Assert.Contains("1.2.4.0", w.Message, StringComparison.Ordinal);
        Assert.Contains(expectedWord, w.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uppercase_sha256_is_accepted()
    {
        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(Entry("1.2.4", sha256: Sha.ToUpperInvariant())), new RecordingLog());

        Assert.Single(result.Document.Releases);
    }

    [Fact]
    public void Default_releasedAt_drops_the_entry()
    {
        var log = new RecordingLog();

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(Entry("1.2.4", releasedAt: default(DateTimeOffset))), log);

        Assert.Empty(result.Document.Releases);
        Assert.True(log.Contains("releasedAt"));
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    public void Rollout_percent_outside_range_is_clamped_with_a_warning(int given, int expected)
    {
        var log = new RecordingLog();

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(Entry("1.2.4", rolloutPercent: given)), log);

        Assert.Equal(expected, Assert.Single(result.Document.Releases).RolloutPercent);
        Assert.True(log.Contains("rolloutPercent"));
    }

    [Fact]
    public void Rollout_percent_within_range_is_untouched_and_not_logged()
    {
        var log = new RecordingLog();

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(Entry("1.2.4", rolloutPercent: 0), Entry("1.2.3", rolloutPercent: 100)), log);

        Assert.Equal([0, 100], result.Document.Releases.Select(r => r.RolloutPercent));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Duplicate_check_runs_after_dropping_invalid_entries()
    {
        // 坏条目与好条目同版本：坏的被丢，剩一条 → 不算重复
        ReleaseFeedDocument doc = Doc(Entry("1.2.4", size: 0), Entry("1.2.4"));

        ValidatedFeed result = ReleaseFeedValidator.Validate(doc, new RecordingLog());

        Assert.Single(result.Document.Releases);
    }

    [Fact]
    public void Entry_with_null_version_from_a_custom_feed_is_dropped_instead_of_throwing()
    {
        // 超出预检的附加项：自带 IReleaseFeed 绕过 Parse，Version 可能为 null；Entry 辅助方法不能造 null，直接构造。
        var log = new RecordingLog();
        var broken = new ReleaseEntry
        {
            Version = null!,
            ReleasedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
            Package = new PackageInfo { Url = "p.zip", Size = 10, Sha256 = Sha },
        };

        ValidatedFeed result = ReleaseFeedValidator.Validate(Doc(broken, Entry("1.2.3")), log);

        Assert.Equal(new Version(1, 2, 3, 0), Assert.Single(result.Document.Releases).Version);
        LogEntry w = Assert.Single(log.AtLevel(UpdateLogLevel.Warning));
        Assert.Contains("version", w.Message, StringComparison.OrdinalIgnoreCase);
    }
}
