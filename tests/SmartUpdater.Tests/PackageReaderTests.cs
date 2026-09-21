using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class PackageReaderTests
{
    private static TestPackage Standard() => new TestPackage { Version = new Version(1, 2, 4) }
        .Add("MyApp.exe", "exe-bytes")
        .Add("appsettings.json", "{ \"a\": 1 }", FilePolicy.Preserve)
        .Add("Resources/logo.png", "png-bytes");

    [Fact]
    public void Valid_package_exposes_manifest_totals_and_entry_streams()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"));

        using PackageContents package = PackageReader.Open(zip);

        Assert.Equal(new Version(1, 2, 4), package.Manifest.Version);
        Assert.Equal(3, package.Files.Count);
        Assert.Equal(9 + 10 + 9, package.TotalFileBytes);
        Assert.Equal(FilePolicy.Preserve, package.Files.Single(f => f.Path == "appsettings.json").Policy);
        using Stream stream = package.OpenEntry("Resources/logo.png");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("png-bytes", reader.ReadToEnd());
    }

    [Fact]
    public void Empty_directory_entries_are_ignored()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a => a.CreateEntry("EmptyDir/"));

        using PackageContents package = PackageReader.Open(zip);

        Assert.Equal(3, package.Files.Count);
    }

    [Fact]
    public void Missing_file_is_a_format_error_with_the_original_exception_inside()
    {
        using var dir = new TempDirectory();

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(dir.Resolve("missing.zip")));

        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void Non_zip_file_is_rejected()
    {
        using var dir = new TempDirectory();
        string notZip = dir.WriteFile("p.zip", "this is not a zip");

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(notZip));

        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    [Fact]
    public void Package_without_manifest_entry_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), omitManifest: true);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains(PackageReader.ManifestEntryName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":1,"version":"not-a-version","files":[]}""")]
    public void Unparsable_manifest_is_rejected_with_JsonException_inside(string manifestJson)
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: manifestJson);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Json_null_manifest_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: "null");

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Unsupported_schema_version_is_rejected(int schemaVersion)
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage { SchemaVersion = schemaVersion }.Add("a.txt", "a").Save(dir.Resolve("p.zip"));

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_schema_version_reads_as_zero_and_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: """{"version":"1.0.0","files":[]}""");

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_files_array_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: """{"schemaVersion":1,"version":"1.0.0","files":null}""");

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("zz96fb92427ae41e4649b934ca495991b7852b855e3b0c44298fc1c149afbf4c89")]
    public void Malformed_sha256_is_rejected(string sha256)
    {
        using var dir = new TempDirectory();
        string json = $$$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":"{{{sha256}}}","size":1}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("sha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_size_is_rejected()
    {
        using var dir = new TempDirectory();
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":"{{TestPackage.Sha256Hex("a")}}","size":-1}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData(".smartupdater/state.json")]
    [InlineData("MyApp.exe.sunew")]
    [InlineData(@"sub\file.dll")]
    [InlineData("CON")]
    public void Unsafe_manifest_path_is_rejected_and_named(string path)
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Add(path, "x").Save(dir.Resolve("p.zip"));

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_insensitive_duplicate_paths_are_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Add("Lib/A.dll", "1").Add("lib/a.dll", "2").Save(dir.Resolve("p.zip"));

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("重复路径", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_path_without_zip_entry_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a => a.GetEntry("Resources/logo.png")!.Delete());

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("Resources/logo.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_entry_not_listed_in_manifest_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a =>
        {
            using Stream s = a.CreateEntry("extra.dll").Open();
            s.Write("x"u8);
        });

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("extra.dll", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_name_case_must_match_the_manifest_exactly()
    {
        using var dir = new TempDirectory();
        string json = JsonSerializer.Serialize(Standard().BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest)
            .Replace("Resources/logo.png", "Resources/Logo.png", StringComparison.Ordinal);
        string zip = Standard().Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));
    }

    [Fact]
    public void Entry_with_backslash_name_is_an_unlisted_entry()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a =>
        {
            using Stream s = a.CreateEntry(@"Resources\evil.dll").Open();
            s.Write("x"u8);
        });

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains(@"Resources\evil.dll", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_size_must_match_manifest_size()
    {
        using var dir = new TempDirectory();
        string json = JsonSerializer.Serialize(Standard().BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest)
            .Replace("\"size\":9,\"policy\":\"replace\"", "\"size\":8,\"policy\":\"replace\"", StringComparison.Ordinal);
        Assert.Contains("\"size\":8", json);   // 确认替换确实发生在 MyApp.exe / logo.png 的条目上
        string zip = Standard().Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejected_package_does_not_keep_the_zip_open()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), omitManifest: true);

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        File.Delete(zip);   // 句柄若未释放，这里会抛 IOException
        Assert.False(File.Exists(zip));
    }

    // ---- zip-slip 攻击样本与畸形输入。契约是"任何格式问题都是 PackageFormatException"，不能漏出别的异常类型。 ----

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("Resources/../../evil.txt")]
    [InlineData(@"..\..\evil.txt")]
    [InlineData(".smartupdater/state.json")]
    [InlineData(".smartupdater/manifest.JSON")]
    public void Zip_slip_entry_names_not_listed_in_manifest_are_rejected_and_named(string entryName)
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a =>
        {
            using Stream s = a.CreateEntry(entryName).Open();
            s.Write("x"u8);
        });

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains(entryName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_path_never_matches_a_differently_spelled_zip_entry()
    {
        using var dir = new TempDirectory();
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":"{{TestPackage.Sha256Hex("x")}}","size":1}]}""";
        string zip = new TestPackage().Add("./a.txt", "x").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("a.txt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_zip_entry_names_are_rejected_instead_of_letting_one_shadow_the_other()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a =>
        {
            using Stream s = a.CreateEntry("MyApp.exe").Open();
            s.Write("evil-exe"u8);
        });

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("重复", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MyApp.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_manifest_entries_are_rejected()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"), customize: a =>
        {
            using Stream s = a.CreateEntry(PackageReader.ManifestEntryName).Open();
            s.Write("""{"schemaVersion":1,"version":"9.9.9","files":[]}"""u8);
        });

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("重复", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PackageReader.ManifestEntryName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void Non_object_or_empty_manifest_is_rejected_with_JsonException_inside(string manifestJson)
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: manifestJson);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Null_version_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: """{"schemaVersion":1,"version":null,"files":[]}""");

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_file_element_is_rejected()
    {
        using var dir = new TempDirectory();
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: """{"schemaVersion":1,"version":"1.0.0","files":[null]}""");

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));
    }

    [Fact]
    public void Null_path_is_rejected_as_empty()
    {
        using var dir = new TempDirectory();
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":null,"sha256":"{{TestPackage.Sha256Hex("a")}}","size":1}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        // RespectNullableAnnotations 使 null path 在 JSON 层被拒，消息为"无法解析"；仍必须被拒绝（PackageFormatException）。
        Assert.Contains("无法解析", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_size_is_reported_as_negative_before_any_entry_comparison()
    {
        using var dir = new TempDirectory();
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":"{{TestPackage.Sha256Hex("a")}}","size":-1}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("负数", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_manifest_path_is_reported_before_the_missing_entry_check()
    {
        using var dir = new TempDirectory();
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"../evil.dll","sha256":"{{TestPackage.Sha256Hex("x")}}","size":1}]}""";
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: json);   // 包里没有任何文件条目

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("含 . 或 .. 段", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("不存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_path_that_is_a_directory_is_reported_as_a_package_format_error()
    {
        using var dir = new TempDirectory();

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(dir.Root));
    }

    [Fact]
    public void Null_sha256_is_rejected()
    {
        using var dir = new TempDirectory();
        string json = """{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":null,"size":1}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("sha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Total_size_overflow_is_rejected_instead_of_wrapping_around()
    {
        using var dir = new TempDirectory();
        string sha = TestPackage.Sha256Hex("a");
        string json = $$"""{"schemaVersion":1,"version":"1.0.0","files":[{"path":"a.txt","sha256":"{{sha}}","size":5000000000000000000},{"path":"b.txt","sha256":"{{sha}}","size":5000000000000000000}]}""";
        string zip = new TestPackage().Add("a.txt", "a").Add("b.txt", "b").Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("总和", ex.Message, StringComparison.Ordinal);
    }

    private const string EmptyManifestJson = """{"schemaVersion":1,"version":"1.0.0","files":[]}""";

    [Fact]
    public void Manifest_of_exactly_the_size_cap_is_accepted()
    {
        using var dir = new TempDirectory();
        string json = EmptyManifestJson + new string(' ', PackageReader.MaxManifestBytes - EmptyManifestJson.Length);
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: json);

        using PackageContents package = PackageReader.Open(zip);

        Assert.Empty(package.Files);
    }

    [Fact]
    public void Manifest_larger_than_the_size_cap_is_rejected_even_though_it_compresses_to_almost_nothing()
    {
        using var dir = new TempDirectory();
        string json = EmptyManifestJson + new string(' ', PackageReader.MaxManifestBytes - EmptyManifestJson.Length + 1);
        string zip = new TestPackage().Save(dir.Resolve("p.zip"), manifestJsonOverride: json);
        Assert.True(new FileInfo(zip).Length < 1024 * 1024, "测试前提：压缩后的 zip 很小，体现的是解压炸弹而非大文件。");

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.Contains("manifest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_manifest_data_is_a_format_error_with_InvalidDataException_inside()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"));
        byte[] bytes = File.ReadAllBytes(zip);
        // 本地文件头：name 在偏移 30，extraLength 在偏移 28。第一次出现 manifest 条目名的位置就是它的本地文件头。
        int nameOffset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(PackageReader.ManifestEntryName));
        Assert.True(nameOffset >= 30);
        int dataStart = nameOffset + PackageReader.ManifestEntryName.Length + BitConverter.ToUInt16(bytes, nameOffset - 2);
        bytes.AsSpan(dataStart, 8).Fill(0xFF);   // DEFLATE 块类型 3 是保留值，解压必然报错
        File.WriteAllBytes(zip, bytes);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(zip));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        File.Delete(zip);   // 出错路径同样不能留下句柄
    }

    [Fact]
    public void OpenEntry_of_a_path_not_in_the_manifest_is_a_programming_error()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"));
        using PackageContents package = PackageReader.Open(zip);

        Assert.Throws<InvalidOperationException>(() => package.OpenEntry("missing.txt"));
        Assert.Throws<InvalidOperationException>(() => package.OpenEntry("myapp.exe"));   // Ordinal：大小写不同即不存在
        Assert.Throws<InvalidOperationException>(() => package.OpenEntry(PackageReader.ManifestEntryName));
    }

    [Fact]
    public void Disposed_package_releases_the_zip_and_refuses_further_reads()
    {
        using var dir = new TempDirectory();
        string zip = Standard().Save(dir.Resolve("p.zip"));
        PackageContents package = PackageReader.Open(zip);

        package.Dispose();
        package.Dispose();   // 幂等

        Assert.Throws<ObjectDisposedException>(() => package.OpenEntry("MyApp.exe"));
        File.Delete(zip);    // 句柄若未释放，这里会抛 IOException
        Assert.False(File.Exists(zip));
    }
}
