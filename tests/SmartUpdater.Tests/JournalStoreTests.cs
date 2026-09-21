using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class JournalStoreTests
{
    private static readonly IFileOperations Fs = PhysicalFileOperations.Instance;

    private static UpdateJournal Journal(JournalState state, string to = "1.1.0") => new()
    {
        State = state,
        FromVersion = new Version(1, 0, 0),
        ToVersion = Version.Parse(to),
        StartedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        Writes = [new JournalEntry { Path = "app.exe", HadTarget = true }],
        Deletes = ["old.dll"],
    };

    private static JournalStore Store(TempDirectory dir, IFileOperations? fs = null, RecordingLog? log = null)
        => new(dir.Resolve(".smartupdater/journal.json"), fs ?? Fs, log ?? new RecordingLog());

    [Fact]
    public void Missing_journal_reads_as_Missing()
    {
        using var dir = new TempDirectory();

        JournalReadResult result = Store(dir).Read();

        Assert.Equal(JournalStatus.Missing, result.Status);
        Assert.Null(result.Journal);
    }

    [Fact]
    public void Full_life_cycle_preparing_committing_done_delete()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        JournalStore store = Store(dir, log: log);

        store.Write(Journal(JournalState.Preparing));
        Assert.Equal(JournalState.Preparing, store.Read().Journal!.State);
        Assert.True(dir.Exists(".smartupdater/journal.json"));

        store.Write(Journal(JournalState.Committing));
        JournalReadResult committing = store.Read();
        Assert.Equal(JournalStatus.Valid, committing.Status);
        Assert.Equal(JournalState.Committing, committing.Journal!.State);
        Assert.Equal(new Version(1, 0, 0), committing.Journal.FromVersion);
        Assert.Equal(new Version(1, 1, 0), committing.Journal.ToVersion);
        Assert.Equal("app.exe", Assert.Single(committing.Journal.Writes).Path);
        Assert.Equal("old.dll", Assert.Single(committing.Journal.Deletes));

        store.Write(Journal(JournalState.Done));
        Assert.Equal(JournalState.Done, store.Read().Journal!.State);

        store.Delete();
        Assert.Equal(JournalStatus.Missing, store.Read().Status);
        Assert.False(dir.Exists(".smartupdater/journal.json" + AtomicFile.TempSuffix));

        Assert.True(log.Contains("preparing → committing"));
        Assert.True(log.Contains("committing → done"));
    }

    // xunit 的 public 测试方法参数不能是 internal 类型（CS0051，含 JournalState?），
    // 所以用枚举成员名传参，方法体内再 Enum.Parse；null 表示磁盘上没有 journal。
    [Theory]
    [InlineData(null, "Committing")]
    [InlineData(null, "Done")]
    [InlineData("Preparing", "Preparing")]
    [InlineData("Preparing", "Done")]
    [InlineData("Committing", "Preparing")]
    [InlineData("Committing", "Committing")]
    [InlineData("Done", "Preparing")]
    [InlineData("Done", "Committing")]
    [InlineData("Done", "Done")]
    public void Illegal_transitions_throw_and_leave_the_file_untouched(string? fromName, string toName)
    {
        JournalState? onDisk = fromName is null ? null : Enum.Parse<JournalState>(fromName);
        JournalState attempted = Enum.Parse<JournalState>(toName);
        using var dir = new TempDirectory();
        JournalStore store = Store(dir);
        if (onDisk is { } existing)
        {
            SeedOnDisk(dir, existing);
        }

        Assert.Throws<InvalidOperationException>(() => store.Write(Journal(attempted)));

        JournalReadResult after = store.Read();
        if (onDisk is { } expected)
        {
            Assert.Equal(expected, after.Journal!.State);
        }
        else
        {
            Assert.Equal(JournalStatus.Missing, after.Status);
        }
    }

    [Fact]
    public void Committing_a_different_target_version_is_rejected()
    {
        using var dir = new TempDirectory();
        JournalStore store = Store(dir);
        store.Write(Journal(JournalState.Preparing, to: "1.1.0"));

        Assert.Throws<InvalidOperationException>(() => store.Write(Journal(JournalState.Committing, to: "1.2.0")));
        Assert.Equal(JournalState.Preparing, store.Read().Journal!.State);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("""{"state":"exploding","toVersion":"1.1.0"}""")]
    public void Corrupt_journal_reads_as_Corrupt_blocks_writes_and_can_be_deleted(string content)
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".smartupdater/journal.json", content);
        var log = new RecordingLog();
        JournalStore store = Store(dir, log: log);

        Assert.Equal(JournalStatus.Corrupt, store.Read().Status);
        Assert.NotEmpty(log.AtLevel(UpdateLogLevel.Error));
        Assert.Throws<InvalidOperationException>(() => store.Write(Journal(JournalState.Preparing)));
        Assert.Equal(content, dir.ReadFile(".smartupdater/journal.json"));

        store.Delete();

        Assert.Equal(JournalStatus.Missing, store.Read().Status);
    }

    [Fact]
    public void Delete_of_missing_journal_does_not_throw()
    {
        using var dir = new TempDirectory();

        Store(dir).Delete();
    }

    [Fact]
    public void Delete_also_removes_a_stale_temp_file()
    {
        using var dir = new TempDirectory();
        Store(dir).Write(Journal(JournalState.Preparing));
        dir.WriteFile(".smartupdater/journal.json" + AtomicFile.TempSuffix, "half-written");

        Store(dir).Delete();

        Assert.False(dir.Exists(".smartupdater/journal.json"));
        Assert.False(dir.Exists(".smartupdater/journal.json" + AtomicFile.TempSuffix));
    }

    // 崩溃扫描：从 preparing 推进到 committing 的任一步崩溃后，journal 必须仍可读，且状态要么还是 preparing、要么已是 committing。
    [Fact]
    public void Transition_crash_at_any_step_never_tears_the_journal()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using var dir = new TempDirectory();
            Store(dir).Write(Journal(JournalState.Preparing));
            var fs = new FaultInjectingFileOperations(Fs) { Policy = FaultInjectingFileOperations.CrashAt(n) };

            try
            {
                Store(dir, fs).Write(Journal(JournalState.Committing));
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            JournalReadResult after = Store(dir).Read();
            Assert.Equal(JournalStatus.Valid, after.Status);
            Assert.True(
                after.Journal!.State is JournalState.Preparing or JournalState.Committing,
                $"崩溃于第 {n} 步（{fs.Operations[^1]}）后状态为 {after.Journal.State}");
        }

        Assert.True(totalSteps >= 5, $"只扫描到 {totalSteps} 步，Write 至少要有读校验 + 四步原子写");
    }

    [Fact]
    public void Write_mutates_the_disk_only_through_the_four_atomic_steps()
    {
        using var dir = new TempDirectory();
        Store(dir).Write(Journal(JournalState.Preparing));
        var fs = new FaultInjectingFileOperations(Fs);

        Store(dir, fs).Write(Journal(JournalState.Committing));

        string[] mutating = [.. fs.MutatingOperations.Select(o => o.Kind)];
        Assert.Equal(new[] { "Delete", "OpenWrite", "FlushToDisk", "Move" }, mutating);
    }

    [Fact]
    public void Done_for_a_different_target_version_than_committing_is_rejected()
    {
        using var dir = new TempDirectory();
        JournalStore store = Store(dir);
        store.Write(Journal(JournalState.Preparing));
        store.Write(Journal(JournalState.Committing));

        Assert.Throws<InvalidOperationException>(() => store.Write(Journal(JournalState.Done, to: "9.9.9")));

        Assert.Equal(JournalState.Committing, store.Read().Journal!.State);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Committing")]
    [InlineData("Done")]
    public void Self_transition_is_rejected_without_touching_the_disk(string stateName)
    {
        JournalState state = Enum.Parse<JournalState>(stateName);
        using var dir = new TempDirectory();
        SeedOnDisk(dir, state);
        byte[] before = File.ReadAllBytes(dir.Resolve(".smartupdater/journal.json"));
        var fs = new FaultInjectingFileOperations(Fs);

        Assert.Throws<InvalidOperationException>(() => Store(dir, fs).Write(Journal(state)));

        Assert.Empty(fs.MutatingOperations);
        Assert.Equal(before, File.ReadAllBytes(dir.Resolve(".smartupdater/journal.json")));
    }

    private static void SeedOnDisk(TempDirectory dir, JournalState state)
    {
        JournalStore seed = Store(dir);
        seed.Write(Journal(JournalState.Preparing));
        if (state >= JournalState.Committing)
        {
            seed.Write(Journal(JournalState.Committing));
        }

        if (state == JournalState.Done)
        {
            seed.Write(Journal(JournalState.Done));
        }
    }
}
