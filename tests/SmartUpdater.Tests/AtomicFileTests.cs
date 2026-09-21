using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class AtomicFileTests
{
    private static readonly IFileOperations Fs = PhysicalFileOperations.Instance;

    [Fact]
    public void Write_creates_the_file_and_leaves_no_temp_file()
    {
        using var dir = new TempDirectory();
        string target = dir.Resolve("state.json");

        AtomicFile.Write(Fs, target, "hello"u8);

        Assert.Equal("hello", File.ReadAllText(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    [Fact]
    public void Write_replaces_existing_content()
    {
        using var dir = new TempDirectory();
        string target = dir.WriteFile("state.json", "OLD");

        AtomicFile.Write(Fs, target, "NEW"u8);

        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void Stale_temp_file_is_ignored_by_reads_and_replaced_by_the_next_write()
    {
        using var dir = new TempDirectory();
        string target = dir.WriteFile("state.json", "OLD");
        dir.WriteFile("state.json" + AtomicFile.TempSuffix, "HALF-WRITTEN");

        Assert.Equal("OLD", System.Text.Encoding.UTF8.GetString(AtomicFile.ReadAllBytes(Fs, target)!));

        AtomicFile.Write(Fs, target, "NEW"u8);

        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.False(File.Exists(target + AtomicFile.TempSuffix));
    }

    // 只读的陈旧临时文件：OpenWrite（FileMode.Create）打不开它，只有写入前先 Delete(tmp)
    // （PhysicalFileOperations.Delete 会先清只读属性）才写得成。
    [Fact]
    public void Stale_read_only_temp_file_is_replaced_by_the_next_write()
    {
        using var dir = new TempDirectory();
        string target = dir.WriteFile("state.json", "OLD");
        string stale = dir.WriteFile("state.json" + AtomicFile.TempSuffix, "HALF-WRITTEN");
        File.SetAttributes(stale, FileAttributes.ReadOnly);

        AtomicFile.Write(Fs, target, "NEW"u8);

        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void ReadAllBytes_returns_null_for_missing_file()
    {
        using var dir = new TempDirectory();

        Assert.Null(AtomicFile.ReadAllBytes(Fs, dir.Resolve("missing.json")));
    }

    [Fact]
    public void Json_round_trips_through_the_source_generated_context()
    {
        using var dir = new TempDirectory();
        string target = dir.Resolve(".smartupdater/manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var manifest = new PackageManifest
        {
            SchemaVersion = 1,
            Version = new Version(1, 2, 4),
            Files = [new ManifestFile { Path = "a.txt", Sha256 = "h", Size = 1, Policy = FilePolicy.Preserve }],
        };

        AtomicFile.WriteJson(Fs, target, manifest, SmartUpdaterJsonContext.Default.PackageManifest);
        PackageManifest? back = AtomicFile.ReadJson(Fs, target, SmartUpdaterJsonContext.Default.PackageManifest);

        Assert.NotNull(back);
        Assert.Equal(new Version(1, 2, 4), back.Version);
        Assert.Equal(FilePolicy.Preserve, Assert.Single(back.Files).Policy);
    }

    [Fact]
    public void ReadJson_returns_null_for_missing_file_and_throws_for_corrupt_content()
    {
        using var dir = new TempDirectory();
        string corrupt = dir.WriteFile("bad.json", "{ this is not json");

        Assert.Null(AtomicFile.ReadJson(Fs, dir.Resolve("missing.json"), SmartUpdaterJsonContext.Default.PackageManifest));
        Assert.Throws<JsonException>(
            () => AtomicFile.ReadJson(Fs, corrupt, SmartUpdaterJsonContext.Default.PackageManifest));
    }

    [Fact]
    public void ReadJson_tolerates_a_utf8_byte_order_mark()
    {
        using var dir = new TempDirectory();
        string path = dir.Resolve("bom.json");
        byte[] json = "{\"schemaVersion\":1,\"version\":\"1.2.3\",\"files\":[]}"u8.ToArray();
        File.WriteAllBytes(path, [.. System.Text.Encoding.UTF8.GetPreamble(), .. json]);

        PackageManifest? back = AtomicFile.ReadJson(Fs, path, SmartUpdaterJsonContext.Default.PackageManifest);

        Assert.NotNull(back);
        Assert.Equal(new Version(1, 2, 3), back.Version);
    }

    // 崩溃点扫描：在写入的每一次磁盘操作之后"断电"，目标文件必须要么是旧内容、要么是新内容，永不残缺或缺失。
    [Fact]
    public void Write_leaves_either_old_or_new_content_when_crashing_at_any_step()
    {
        int totalSteps = 0;

        for (int n = 1; ; n++)
        {
            using var dir = new TempDirectory();
            string target = dir.WriteFile("state.json", "OLD");
            var fs = new FaultInjectingFileOperations(PhysicalFileOperations.Instance)
            {
                Policy = FaultInjectingFileOperations.CrashAt(n),
            };

            try
            {
                AtomicFile.Write(fs, target, "NEW"u8);
            }
            catch (SimulatedCrashException)
            {
            }

            if (!fs.Crashed)
            {
                totalSteps = n - 1;
                break;
            }

            string content = File.ReadAllText(target);
            Assert.True(content is "OLD" or "NEW", $"崩溃于第 {n} 步（{fs.Operations[^1]}）后目标内容为 '{content}'");
        }

        // Delete(tmp) → OpenWrite(tmp) → FlushToDisk → Move(tmp → target)。写入步骤变了，这里的期望步数要同步更新。
        Assert.Equal(4, totalSteps);
    }

    [Fact]
    public void Write_operation_sequence_is_delete_temp_open_flush_move()
    {
        using var dir = new TempDirectory();
        string target = dir.Resolve("state.json");
        var fs = new FaultInjectingFileOperations(PhysicalFileOperations.Instance);

        AtomicFile.Write(fs, target, "x"u8);

        string[] kinds = [.. fs.Operations.Select(o => o.Kind)];
        Assert.Equal(new[] { "Delete", "OpenWrite", "FlushToDisk", "Move" }, kinds);
        Assert.Equal(target + AtomicFile.TempSuffix, fs.Operations[0].Path);
        Assert.Equal(target + AtomicFile.TempSuffix, fs.Operations[3].Path);
        Assert.Equal(target, fs.Operations[3].SecondPath);
    }

    [Fact]
    public void FailOnce_policy_fires_exactly_once()
    {
        using var dir = new TempDirectory();
        var fs = new FaultInjectingFileOperations(PhysicalFileOperations.Instance)
        {
            // Move 的 op.Path 是源路径（AtomicFile 的那次 Move 源是 s.json.tmp），所以后缀要带 .tmp
            Policy = FaultInjectingFileOperations.FailOnce("Move", ".json" + AtomicFile.TempSuffix),
        };
        string target = dir.Resolve("s.json");

        Assert.Throws<IOException>(() => AtomicFile.Write(fs, target, "1"u8));
        AtomicFile.Write(fs, target, "2"u8);

        Assert.Equal("2", File.ReadAllText(target));
        Assert.False(fs.Crashed);
    }
}
