using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class PackageBuilderTests
{
    private static BuildResult Build(FakeApp app, Version version, params string[] preserve)
        => PackageBuilder.Build(new BuildRequest(app.Root, version, preserve));

    private static string ReadManifestJson(BuildResult result)
    {
        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry(PackageReader.ManifestEntryName)!;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Manifest_lists_every_file_with_hash_size_and_forward_slashes()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 2, 4));

        Assert.Equal(1, result.Manifest.SchemaVersion);
        Assert.Equal(new Version(1, 2, 4), result.Manifest.Version);
        Assert.Equal(
            ["MyApp.dll", "MyApp.exe", "Resources/logo.png", "appsettings.json"],
            result.Manifest.Files.Select(f => f.Path));

        ManifestFile exe = result.Manifest.Files.Single(f => f.Path == "MyApp.exe");
        Assert.Equal(FakeApp.Sha256Hex("exe v1"), exe.Sha256);
        Assert.Equal(6, exe.Size);
        Assert.Equal(FilePolicy.Replace, exe.Policy);
    }

    [Fact]
    public void Hashes_are_lowercase_hex()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0, 0));

        Assert.All(result.Manifest.Files, f =>
        {
            Assert.Equal(64, f.Sha256.Length);
            Assert.Equal(f.Sha256.ToLowerInvariant(), f.Sha256);
        });
    }

    [Fact]
    public void Files_are_sorted_ordinal_by_path()
    {
        using FakeApp app = new FakeApp()
            .With("b.txt", "b")
            .With("a.txt", "a")
            .With("Z.txt", "z")
            .With("sub/c.txt", "c");

        BuildResult result = Build(app, new Version(1, 0));

        // Ordinal：大写字母排在小写字母之前。
        Assert.Equal(["Z.txt", "a.txt", "b.txt", "sub/c.txt"], result.Manifest.Files.Select(f => f.Path));
    }

    [Fact]
    public void Preserve_patterns_set_the_policy()
    {
        using FakeApp app = FakeApp.Standard().With("data/users.db", "db");

        BuildResult result = Build(app, new Version(1, 0), "appsettings.json", "data/**");

        Assert.Equal(FilePolicy.Preserve, result.Manifest.Files.Single(f => f.Path == "appsettings.json").Policy);
        Assert.Equal(FilePolicy.Preserve, result.Manifest.Files.Single(f => f.Path == "data/users.db").Policy);
        Assert.Equal(FilePolicy.Replace, result.Manifest.Files.Single(f => f.Path == "MyApp.exe").Policy);
    }

    [Fact]
    public void Preserve_globs_are_matched_against_forward_slash_relative_paths()
    {
        // GlobMatcher.Matches 不做分隔符归一：若 Builder 把 Windows 的反斜杠路径原样传进去，
        // "config/*.json" 一个都匹配不上（模式里的 / 对不上路径里的 \）。
        using FakeApp app = new FakeApp()
            .With("top.json", "t")
            .With("config/a.json", "a")
            .With("config/sub/b.json", "b");

        BuildResult result = Build(app, new Version(1, 0), "config/*.json");

        Assert.Equal(FilePolicy.Preserve, result.Manifest.Files.Single(f => f.Path == "config/a.json").Policy);
        Assert.Equal(FilePolicy.Replace, result.Manifest.Files.Single(f => f.Path == "config/sub/b.json").Policy);
        Assert.Equal(FilePolicy.Replace, result.Manifest.Files.Single(f => f.Path == "top.json").Policy);
    }

    [Fact]
    public void A_single_star_glob_does_not_cross_directories()
    {
        // 反过来：若传进去的是反斜杠路径，"*.json" 的 [^/]* 会把 \ 吃掉，config\a.json 被静默当成命中。
        using FakeApp app = new FakeApp()
            .With("top.json", "t")
            .With("config/a.json", "a");

        BuildResult result = Build(app, new Version(1, 0), "*.json");

        Assert.Equal(FilePolicy.Preserve, result.Manifest.Files.Single(f => f.Path == "top.json").Policy);
        Assert.Equal(FilePolicy.Replace, result.Manifest.Files.Single(f => f.Path == "config/a.json").Policy);
    }

    [Fact]
    public void Smartupdater_directory_is_excluded_entirely()
    {
        using FakeApp app = FakeApp.Standard()
            .With(".smartupdater/state.json", """{"deviceGuid":"x"}""")
            .With(".smartupdater/logs/updater-20260919.log", "log line")
            .With(".smartupdater/reports.jsonl", "{}");

        BuildResult result = Build(app, new Version(1, 0));

        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.StartsWith(".smartupdater", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, result.Manifest.Files.Count);
    }

    [Fact]
    public void Smartupdater_directory_exclusion_is_case_insensitive()
    {
        using FakeApp app = FakeApp.Standard().With(".SmartUpdater/state.json", "{}");

        BuildResult result = Build(app, new Version(1, 0));

        Assert.Equal(4, result.Manifest.Files.Count);
        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.Contains("state.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Exclusion_compares_path_segments_not_substrings()
    {
        // 按路径段比较。名字里带 ".smartupdater" 但首段不等于它的，都是应用自己的文件，必须进包。
        using FakeApp app = FakeApp.Standard()
            .With("my.smartupdater.config", "cfg")
            .With(".smartupdater-old/a.txt", "old");

        BuildResult result = Build(app, new Version(1, 0));

        Assert.Contains(result.Manifest.Files, f => f.Path == "my.smartupdater.config");
        Assert.Contains(result.Manifest.Files, f => f.Path == ".smartupdater-old/a.txt");
        Assert.Equal(6, result.Manifest.Files.Count);
    }

    [Fact]
    public void Zip_contains_the_manifest_at_the_documented_entry_name()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 2, 4));

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        ZipArchiveEntry manifestEntry = Assert.Single(
            archive.Entries, e => e.FullName == ".smartupdater/manifest.json");

        using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
        PackageManifest? roundTripped = JsonSerializer.Deserialize(
            reader.ReadToEnd(), SmartUpdaterJsonContext.Default.PackageManifest);

        Assert.NotNull(roundTripped);
        Assert.Equal(new Version(1, 2, 4), roundTripped.Version);
        Assert.Equal(result.Manifest.Files.Count, roundTripped.Files.Count);
    }

    [Fact]
    public void Manifest_json_keys_are_camel_case()
    {
        // 源生成上下文的 [JsonSourceGenerationOptions] 只作用于 Default 实例；
        // 用自建 options 序列化会静默退回 PascalCase，客户端因缺 version 拒包。
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 2, 4));

        using JsonDocument document = JsonDocument.Parse(ReadManifestJson(result));
        JsonElement root = document.RootElement;
        Assert.Equal(["schemaVersion", "version", "files"], root.EnumerateObject().Select(p => p.Name));

        JsonElement first = root.GetProperty("files")[0];
        Assert.Equal(["path", "sha256", "size", "policy"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("replace", first.GetProperty("policy").GetString());
        Assert.Equal("1.2.4", root.GetProperty("version").GetString());
    }

    [Fact]
    public void Manifest_json_is_indented_with_two_spaces_and_keeps_non_ascii_literal()
    {
        using FakeApp app = FakeApp.Standard().With("资源/图标+1.png", "icon");

        string json = ReadManifestJson(Build(app, new Version(1, 0)));

        // 换行固定为 \n：Utf8JsonWriter 默认取 Environment.NewLine，Windows 上会写出 \r\n，
        // 同一输入在两个平台会打出不同字节的包。
        Assert.DoesNotContain('\r', json);
        Assert.Contains("\n  \"schemaVersion\": 1,", json, StringComparison.Ordinal);
        Assert.Contains("\n    {\n      \"path\": ", json, StringComparison.Ordinal);
        Assert.Contains("资源/图标+1.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_does_not_list_itself()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0));

        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.Contains("manifest.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Zip_entries_use_forward_slashes_and_are_sorted_with_the_manifest_last()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0));

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        string[] names = [.. archive.Entries.Select(e => e.FullName)];

        Assert.Equal(
            ["MyApp.dll", "MyApp.exe", "Resources/logo.png", "appsettings.json", ".smartupdater/manifest.json"],
            names);
        Assert.All(names, n => Assert.DoesNotContain('\\', n));
    }

    [Fact]
    public void Entry_timestamps_are_fixed()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0));

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);

        // zip 的 DOS 时间不带时区：读回时墙钟不变、offset 变成本机的。比 .DateTime，不比 DateTimeOffset。
        Assert.All(archive.Entries, e => Assert.Equal(
            PackageBuilder.FixedEntryTimestamp.DateTime, e.LastWriteTime.DateTime));
    }

    [Fact]
    public void Entries_are_deflated_at_the_optimal_level()
    {
        // 压缩级别会改变全部包的字节，"两次构建相同"的测试碰不到它（同一进程内两次用的是同一级别）。
        // 好在 zip 中央目录的通用标志位 1–2 记录了 deflate 级别：Optimal = 00，SmallestSize = 01，Fastest = 11。
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0));

        CentralDirectoryEntry[] entries = ReadCentralDirectory(result.ZipBytes);
        Assert.Equal(5, entries.Length);
        Assert.All(entries, e =>
        {
            Assert.Equal(8, e.CompressionMethod);
            Assert.Equal(0, e.GeneralPurposeFlags & 0x0006);
        });
    }

    private readonly record struct CentralDirectoryEntry(string Name, ushort GeneralPurposeFlags, ushort CompressionMethod);

    private static CentralDirectoryEntry[] ReadCentralDirectory(byte[] zip)
    {
        // 没有 zip 注释时，EOCD 记录（22 字节）就在文件末尾。
        int eocd = zip.Length - 22;
        Assert.Equal(0x06054b50u, BitConverter.ToUInt32(zip, eocd));
        int count = BitConverter.ToUInt16(zip, eocd + 10);
        int offset = (int)BitConverter.ToUInt32(zip, eocd + 16);

        var entries = new CentralDirectoryEntry[count];
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0x02014b50u, BitConverter.ToUInt32(zip, offset));
            ushort flags = BitConverter.ToUInt16(zip, offset + 8);
            ushort method = BitConverter.ToUInt16(zip, offset + 10);
            int nameLength = BitConverter.ToUInt16(zip, offset + 28);
            int extraLength = BitConverter.ToUInt16(zip, offset + 30);
            int commentLength = BitConverter.ToUInt16(zip, offset + 32);
            entries[i] = new CentralDirectoryEntry(Encoding.UTF8.GetString(zip, offset + 46, nameLength), flags, method);
            offset += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    [Fact]
    public void Two_builds_of_the_same_input_are_byte_identical()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult first = Build(app, new Version(1, 2, 4));
        app.TouchAll();
        BuildResult second = Build(app, new Version(1, 2, 4));

        Assert.Equal(first.ZipBytes, second.ZipBytes);
        Assert.Equal(first.Sha256Hex, second.Sha256Hex);
    }

    [Fact]
    public void Changing_a_file_changes_the_package_hash()
    {
        using FakeApp app = FakeApp.Standard();
        BuildResult before = Build(app, new Version(1, 2, 4));

        app.With("MyApp.exe", "exe v2");
        BuildResult after = Build(app, new Version(1, 2, 4));

        Assert.NotEqual(before.Sha256Hex, after.Sha256Hex);
    }

    [Fact]
    public void Changing_only_the_version_changes_the_package_hash()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult a = Build(app, new Version(1, 2, 4));
        BuildResult b = Build(app, new Version(1, 2, 5));

        Assert.NotEqual(a.Sha256Hex, b.Sha256Hex);
    }

    [Fact]
    public void Reported_hash_matches_the_zip_bytes()
    {
        using FakeApp app = FakeApp.Standard();

        BuildResult result = Build(app, new Version(1, 0));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(result.ZipBytes)), result.Sha256Hex);
    }

    [Fact]
    public void Version_string_is_written_verbatim_without_padding()
    {
        using FakeApp app = FakeApp.Standard();

        Assert.Equal("1.2.4", Build(app, new Version("1.2.4")).Manifest.Version.ToString());
        Assert.Equal("1.2.4.0", Build(app, new Version("1.2.4.0")).Manifest.Version.ToString());
    }

    [Fact]
    public void Version_string_is_written_verbatim_into_the_manifest_json()
    {
        // 上一条只看内存里的 Manifest；这里看真正落进 zip 的 JSON。
        using FakeApp app = FakeApp.Standard();

        using JsonDocument three = JsonDocument.Parse(ReadManifestJson(Build(app, new Version("1.2.4"))));
        using JsonDocument four = JsonDocument.Parse(ReadManifestJson(Build(app, new Version("1.2.4.0"))));

        Assert.Equal("1.2.4", three.RootElement.GetProperty("version").GetString());
        Assert.Equal("1.2.4.0", four.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Unsafe_paths_are_rejected()
    {
        using FakeApp app = FakeApp.Standard();
        // 尾部带点的文件名建不出来（Win32 会把点剥掉），真实目录扫描下可达的是保留后缀：
        // 从安装目录打包时确实会捡到升级残留的 .suold。
        app.With("legacy.dll.suold", "x");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Build(app, new Version(1, 0)));

        Assert.Contains("legacy.dll.suold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_unsafe_paths_are_reported_in_one_error()
    {
        using FakeApp app = FakeApp.Standard()
            .With("legacy.dll.suold", "x")
            .With("sub/other.exe.sunew", "y");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Build(app, new Version(1, 0)));

        Assert.Contains("legacy.dll.suold", error.Message, StringComparison.Ordinal);
        Assert.Contains("sub/other.exe.sunew", error.Message, StringComparison.Ordinal);
    }

    // ---- 路径校验的接缝：NTFS 上建不出只差大小写的两个文件，所以直接喂路径列表 ----

    private static ManifestFile ManifestEntry(string path)
        => new() { Path = path, Sha256 = new string('0', 64), Size = 0 };

    [Fact]
    public void Paths_differing_only_by_case_are_rejected_like_the_client_does()
    {
        // 客户端 PackageReader 用 ManifestPathValidator.Validate 整份校验，会拒绝不分大小写的重复路径；
        // 大小写敏感的文件系统（Linux / WSL）上这样的两个文件都能存在，Packer 必须在打包时就挡住。
        ManifestFile[] files = [ManifestEntry("Docs/a.txt"), ManifestEntry("docs/A.txt")];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.EnsureManifestPathsAreValid(files));

        Assert.Contains("docs/A.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("重复路径", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_path_problem_is_reported_in_one_error_including_duplicates()
    {
        ManifestFile[] files =
        [
            ManifestEntry("Docs/a.txt"),
            ManifestEntry("docs/A.txt"),
            ManifestEntry("legacy.dll.suold"),
        ];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.EnsureManifestPathsAreValid(files));

        Assert.Contains("docs/A.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("legacy.dll.suold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_safe_paths_pass_the_manifest_path_check()
    {
        ManifestFile[] files = [ManifestEntry("Docs/a.txt"), ManifestEntry("docs/b.txt"), ManifestEntry("a.txt")];

        PackageBuilder.EnsureManifestPathsAreValid(files);
    }

    [Fact]
    public void Empty_input_directory_is_rejected()
    {
        using var temp = new TempDirectory();

        Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.Build(new BuildRequest(temp.Path, new Version(1, 0), [])));
    }

    [Fact]
    public void Input_directory_containing_only_smartupdater_state_is_rejected()
    {
        using FakeApp app = new FakeApp().With(".smartupdater/state.json", "{}");

        Assert.Throws<InvalidOperationException>(() => Build(app, new Version(1, 0)));
    }

    [Fact]
    public void Missing_input_directory_is_rejected()
    {
        string missing = Path.Combine(Path.GetTempPath(), "supack-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(
            () => PackageBuilder.Build(new BuildRequest(missing, new Version(1, 0), [])));
    }

    [Fact]
    public void Empty_directories_produce_no_entries()
    {
        using FakeApp app = FakeApp.Standard();
        Directory.CreateDirectory(Path.Combine(app.Root, "EmptyFolder"));

        BuildResult result = Build(app, new Version(1, 0));

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("EmptyFolder", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_byte_files_round_trip()
    {
        using FakeApp app = FakeApp.Standard().WithBytes("empty.dat", []);

        BuildResult result = Build(app, new Version(1, 0));

        ManifestFile empty = result.Manifest.Files.Single(f => f.Path == "empty.dat");
        Assert.Equal(0, empty.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), empty.Sha256);
    }

    [Fact]
    public void Non_ascii_file_names_round_trip()
    {
        using FakeApp app = FakeApp.Standard().With("资源/图标.png", "icon");

        BuildResult result = Build(app, new Version(1, 0));

        Assert.Contains(result.Manifest.Files, f => f.Path == "资源/图标.png");

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "资源/图标.png");
    }

    // ---- 反向校验：用客户端库的读取器确认"打出来的包客户端真的能吃" ----

    [Fact]
    public void Package_is_accepted_by_the_client_package_reader()
    {
        using FakeApp app = FakeApp.Standard().With("data/users.db", "db");
        using var temp = new TempDirectory();
        string zipPath = Path.Combine(temp.Path, "MyApp-1.2.4.zip");

        BuildResult result = Build(app, new Version(1, 2, 4), "appsettings.json");
        File.WriteAllBytes(zipPath, result.ZipBytes);

        using PackageContents contents = PackageReader.Open(zipPath);

        Assert.Equal(new Version(1, 2, 4), contents.Manifest.Version);
        Assert.Equal(result.Manifest.Files.Count, contents.Files.Count);
        Assert.Equal(result.Manifest.Files.Sum(f => f.Size), contents.TotalFileBytes);
        Assert.Equal(
            FilePolicy.Preserve,
            contents.Files.Single(f => f.Path == "appsettings.json").Policy);
    }

    [Fact]
    public void Every_manifest_entry_can_be_opened_and_matches_its_hash()
    {
        using FakeApp app = FakeApp.Standard();
        using var temp = new TempDirectory();
        string zipPath = Path.Combine(temp.Path, "p.zip");

        File.WriteAllBytes(zipPath, Build(app, new Version(1, 0)).ZipBytes);

        using PackageContents contents = PackageReader.Open(zipPath);
        foreach (ManifestFile file in contents.Files)
        {
            using Stream entry = contents.OpenEntry(file.Path);
            using var buffer = new MemoryStream();
            entry.CopyTo(buffer);

            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
            Assert.Equal(file.Size, buffer.Length);
        }
    }
}
