using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateStateStoreTests
{
    private static readonly IFileOperations Fs = PhysicalFileOperations.Instance;

    private static UpdateStateStore Store(TempDirectory dir, RecordingLog? log = null, IFileOperations? fs = null)
        => new(dir.Resolve(".smartupdater/state.json"), fs ?? Fs, log ?? new RecordingLog());

    [Fact]
    public void Missing_file_creates_fresh_state_with_a_persistent_device_guid()
    {
        using var dir = new TempDirectory();

        UpdateState first = Store(dir).Load();
        UpdateState second = Store(dir).Load();

        Assert.NotEqual(Guid.Empty, first.DeviceGuid);
        Assert.Equal(first.DeviceGuid, second.DeviceGuid);
        Assert.Null(first.CurrentVersion);
        Assert.True(dir.Exists(".smartupdater/state.json"));
    }

    // 文件不存在（新建）与文件内容为 null / 损坏（改名保留）是两条不同的路径，缺失的文件不许被当成损坏处理。
    [Fact]
    public void Missing_file_is_created_without_a_warning_or_a_backup()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();

        Store(dir, log).Load();

        Assert.Empty(log.AtLevel(UpdateLogLevel.Warning));
        Assert.False(dir.Exists(".smartupdater/state.json" + UpdateStateStore.CorruptBackupSuffix));
        Assert.Contains(log.AtLevel(UpdateLogLevel.Information), e => e.Message.Contains("state.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_replaced()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", "{ not json");
        var log = new RecordingLog();

        UpdateState state = Store(dir, log).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.Equal("{ not json", dir.ReadFile(".smartupdater/state.json" + UpdateStateStore.CorruptBackupSuffix));
        Assert.NotEqual("{ not json", dir.ReadFile(".smartupdater/state.json"));
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains(UpdateStateStore.CorruptBackupSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Json_null_literal_is_treated_as_corrupt()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", "null");

        UpdateState state = Store(dir).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.True(dir.Exists(".smartupdater/state.json" + UpdateStateStore.CorruptBackupSuffix));
    }

    [Fact]
    public void Empty_device_guid_is_repaired_and_persisted()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """{"currentVersion":"1.0.0"}""");

        UpdateState state = Store(dir).Load();
        UpdateState again = Store(dir).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.Equal(state.DeviceGuid, again.DeviceGuid);
        Assert.Equal(new Version(1, 0, 0), again.CurrentVersion);
    }

    [Fact]
    public void Skipped_versions_at_or_below_current_are_pruned_on_load()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """
            {"currentVersion":"1.2.4","deviceGuid":"11111111-2222-3333-4444-555555555555",
             "skippedVersions":["1.2.3","1.2.4","1.3.0","1.3.0"]}
            """);

        UpdateState state = Store(dir).Load();
        UpdateState reloaded = Store(dir).Load();

        Assert.Equal(new[] { new Version(1, 3, 0) }, state.SkippedVersions);
        Assert.Equal(new[] { new Version(1, 3, 0) }, reloaded.SkippedVersions);
    }

    [Fact]
    public void TrySkipVersion_refuses_the_current_version_and_duplicates()
    {
        var state = new UpdateState { CurrentVersion = new Version(1, 2, 4), DeviceGuid = Guid.NewGuid() };

        Assert.False(state.TrySkipVersion(new Version(1, 2, 4)));
        Assert.True(state.TrySkipVersion(new Version(1, 3, 0)));
        Assert.False(state.TrySkipVersion(new Version(1, 3, 0)));
        Assert.Equal(new[] { new Version(1, 3, 0) }, state.SkippedVersions);
    }

    [Fact]
    public void Selection_set_excludes_the_current_version_even_when_the_file_lists_it()
    {
        var state = new UpdateState
        {
            CurrentVersion = new Version(1, 2, 4),
            SkippedVersions = [new Version(1, 2, 4), new Version(1, 3, 0)],
        };

        IReadOnlySet<Version> set = state.GetSkippedVersionsForSelection();

        Assert.DoesNotContain(new Version(1, 2, 4), set);
        Assert.Contains(new Version(1, 3, 0), set);
    }

    [Fact]
    public void Save_creates_the_state_directory_and_leaves_no_temp_file()
    {
        using var dir = new TempDirectory();
        var state = new UpdateState { DeviceGuid = Guid.NewGuid(), CurrentVersion = new Version(2, 0) };

        Store(dir).Save(state);

        Assert.True(dir.Exists(".smartupdater/state.json"));
        Assert.False(dir.Exists(".smartupdater/state.json" + AtomicFile.TempSuffix));
    }

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        using var dir = new TempDirectory();
        var state = new UpdateState
        {
            CurrentVersion = new Version(1, 2, 4),
            DeviceGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            SkippedVersions = [new Version(1, 3, 0)],
            FeedETag = "\"etag\"",
            LastCheckedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
            LastReportedAt = new DateTimeOffset(2026, 9, 18, 9, 0, 0, TimeSpan.Zero),
        };

        Store(dir).Save(state);
        UpdateState back = Store(dir).Load();

        Assert.Equal(state.CurrentVersion, back.CurrentVersion);
        Assert.Equal(state.DeviceGuid, back.DeviceGuid);
        Assert.Equal(state.SkippedVersions, back.SkippedVersions);
        Assert.Equal(state.FeedETag, back.FeedETag);
        Assert.Equal(state.LastCheckedAt, back.LastCheckedAt);
        Assert.Equal(state.LastReportedAt, back.LastReportedAt);
    }

    [Fact]
    public void Save_failure_during_load_is_logged_not_thrown()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Fs)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenWrite", AtomicFile.TempSuffix),
        };

        UpdateState state = Store(dir, log, fs).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Warning));
        Assert.False(dir.Exists(".smartupdater/state.json"));
    }

    [Fact]
    public void Save_failure_outside_load_propagates()
    {
        using var dir = new TempDirectory();
        var fs = new FaultInjectingFileOperations(Fs)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenWrite", AtomicFile.TempSuffix),
        };

        Assert.Throws<IOException>(() => Store(dir, fs: fs).Save(new UpdateState { DeviceGuid = Guid.NewGuid() }));
    }

    // ---- "Load 永不抛出" 的契约 ----

    // 操作序列测试把一次 state 保存钉成恰好 4 个 mutating 操作，
    // 所以健康状态的 Load 不许有任何写盘动作。
    [Fact]
    public void Healthy_load_performs_no_mutating_operations()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """
            {"currentVersion":"1.2.4","deviceGuid":"11111111-2222-3333-4444-555555555555",
             "skippedVersions":["1.3.0","1.4.0"],"feedETag":"\"e\""}
            """);
        var fs = new FaultInjectingFileOperations(Fs);

        UpdateState state = Store(dir, fs: fs).Load();

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), state.DeviceGuid);
        Assert.Equal(new[] { new Version(1, 3, 0), new Version(1, 4, 0) }, state.SkippedVersions);
        Assert.Contains(fs.Operations, o => o.Kind == "OpenRead");
        Assert.Empty(fs.MutatingOperations);
    }

    [Fact]
    public void Save_into_an_existing_directory_performs_exactly_the_four_atomic_write_operations()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/other.txt", "x");
        var fs = new FaultInjectingFileOperations(Fs);

        Store(dir, fs: fs).Save(new UpdateState { DeviceGuid = Guid.NewGuid() });

        Assert.Equal(
            new[] { "Delete", "OpenWrite", "FlushToDisk", "Move" },
            fs.MutatingOperations.Select(o => o.Kind));
        Assert.DoesNotContain(fs.Operations, o => o.Kind == "CreateDirectory");
    }

    [Fact]
    public void Save_into_a_missing_directory_creates_it_first()
    {
        using var dir = new TempDirectory();
        var fs = new FaultInjectingFileOperations(Fs);

        Store(dir, fs: fs).Save(new UpdateState { DeviceGuid = Guid.NewGuid() });

        Assert.Equal(
            new[] { "CreateDirectory", "Delete", "OpenWrite", "FlushToDisk", "Move" },
            fs.MutatingOperations.Select(o => o.Kind));
    }

    // 手改坏的文件里 "skippedVersions":[null] 会反序列化成带 null 元素的列表；丢弃 null 元素并回写。
    [Fact]
    public void Null_elements_in_skipped_versions_are_dropped_and_persisted()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """
            {"currentVersion":"1.2.4","deviceGuid":"11111111-2222-3333-4444-555555555555",
             "skippedVersions":[null,"1.3.0",null]}
            """);

        UpdateState state = Store(dir).Load();
        UpdateState reloaded = Store(dir).Load();

        Assert.Equal(new[] { new Version(1, 3, 0) }, state.SkippedVersions);
        Assert.Equal(new[] { new Version(1, 3, 0) }, reloaded.SkippedVersions);
        Assert.DoesNotContain(state.SkippedVersions, v => v is null);
    }

    [Fact]
    public void Null_elements_are_dropped_even_when_there_is_no_current_version()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """
            {"deviceGuid":"11111111-2222-3333-4444-555555555555","skippedVersions":[null,"1.3.0"]}
            """);

        UpdateState state = Store(dir).Load();

        Assert.Equal(new[] { new Version(1, 3, 0) }, state.SkippedVersions);
    }

    [Fact]
    public void Null_skipped_versions_list_is_repaired_to_empty()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """
            {"currentVersion":"1.2.4","deviceGuid":"11111111-2222-3333-4444-555555555555","skippedVersions":null}
            """);

        UpdateState state = Store(dir).Load();

        Assert.NotNull(state.SkippedVersions);
        Assert.Empty(state.SkippedVersions);
        Assert.True(state.TrySkipVersion(new Version(1, 3, 0)));
    }

    [Fact]
    public void Selection_set_ignores_null_elements()
    {
        var state = new UpdateState
        {
            CurrentVersion = new Version(1, 2, 4),
            SkippedVersions = [null!, new Version(1, 3, 0)],
        };

        IReadOnlySet<Version> set = state.GetSkippedVersionsForSelection();

        Assert.Equal(new[] { new Version(1, 3, 0) }, set);
    }

    [Fact]
    public void Unreadable_file_yields_in_memory_state_without_overwriting_it()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", """{"currentVersion":"1.2.4","deviceGuid":"11111111-2222-3333-4444-555555555555"}""");
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Fs)
        {
            Policy = FaultInjectingFileOperations.FailAlways("OpenRead", "state.json"),
        };

        UpdateState state = Store(dir, log, fs).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Warning));
        Assert.Empty(fs.MutatingOperations);
        Assert.Contains("1.2.4", dir.ReadFile(".smartupdater/state.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_to_move_a_corrupt_file_aside_is_logged_not_thrown()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/state.json", "{ not json");
        var log = new RecordingLog();
        var fs = new FaultInjectingFileOperations(Fs)
        {
            // 匹配的是 Move 的源路径：只命中"改名为 .bad"，不影响 AtomicFile 的 state.json.tmp → state.json。
            Policy = FaultInjectingFileOperations.FailAlways("Move", "state.json"),
        };

        UpdateState state = Store(dir, log, fs).Load();

        Assert.NotEqual(Guid.Empty, state.DeviceGuid);
        Assert.Contains(log.AtLevel(UpdateLogLevel.Warning), e => e.Message.Contains(UpdateStateStore.CorruptBackupSuffix, StringComparison.Ordinal));
    }
}
