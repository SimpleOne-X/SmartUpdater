using System.IO.Compression;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class PackCommandTests
{
    private static int Run(out string stdout, out string stderr, params string[] args)
    {
        Assert.True(ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? parseError), parseError);

        var output = new StringWriter();
        var error = new StringWriter();
        int code = PackCommand.Run(parsed!, output, error);

        stdout = output.ToString();
        stderr = error.ToString();
        return code;
    }

    /// <summary>
    /// stderr 的第一行，即错误行本身。选项名要在这一行里断言：未知选项会在错误行之后附整张用法表，
    /// 用法表里有全部选项名，对整个 stderr 做 Contains 会被它"白送"通过。
    /// </summary>
    private static string ErrorLine(string stderr)
    {
        string line = stderr.ReplaceLineEndings("\n").Split('\n')[0];
        Assert.NotEmpty(line);
        return line;
    }

    private static JsonNode ReadFeed(string outputDirectory)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(outputDirectory, "releases.json")))!;

    [Fact]
    public void Pack_writes_the_documented_layout()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr,
            "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path,
            "--package-name", "MyApp", "--released-at", "2026-09-18T10:00:00Z");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(stderr);
        Assert.True(File.Exists(Path.Combine(output.Path, "releases.json")));
        Assert.True(File.Exists(Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip")));
    }

    [Fact]
    public void Feed_entry_points_at_the_package_with_its_real_hash_and_size()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        string zipPath = Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip");
        byte[] zip = File.ReadAllBytes(zipPath);

        JsonNode package = ReadFeed(output.Path)["releases"]!.AsArray()[0]!["package"]!;
        Assert.Equal("packages/MyApp-1.2.4.zip", package["url"]!.GetValue<string>());
        Assert.Equal(zip.Length, package["size"]!.GetValue<long>());
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)),
            package["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Package_name_defaults_to_the_single_root_executable()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path);

        Assert.True(File.Exists(Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip")));
    }

    [Fact]
    public void Package_name_is_required_when_there_is_no_single_root_executable()
    {
        using FakeApp app = FakeApp.Standard().With("Other.exe", "other");
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr,
            "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--package-name", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Preserve_patterns_reach_the_manifest_inside_the_zip()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path,
            "--package-name", "MyApp", "--preserve", "appsettings.json");

        using var archive = ZipFile.OpenRead(Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip"));
        using var reader = new StreamReader(archive.GetEntry(".smartupdater/manifest.json")!.Open());
        PackageManifest manifest = System.Text.Json.JsonSerializer.Deserialize(
            reader.ReadToEnd(), SmartUpdaterJsonContext.Default.PackageManifest)!;

        Assert.Equal(FilePolicy.Preserve, manifest.Files.Single(f => f.Path == "appsettings.json").Policy);
        Assert.Equal(FilePolicy.Replace, manifest.Files.Single(f => f.Path == "MyApp.exe").Policy);
    }

    [Fact]
    public void Two_packs_of_the_same_input_produce_the_same_package_hash()
    {
        using FakeApp app = FakeApp.Standard();
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", first.Path, "--package-name", "MyApp", "--released-at", "2026-09-18T10:00:00Z");
        app.TouchAll();
        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", second.Path, "--package-name", "MyApp", "--released-at", "2026-09-18T10:00:00Z");

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(first.Path, "packages", "MyApp-1.2.4.zip")),
            File.ReadAllBytes(Path.Combine(second.Path, "packages", "MyApp-1.2.4.zip")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(first.Path, "releases.json")),
            File.ReadAllText(Path.Combine(second.Path, "releases.json")));
    }

    [Fact]
    public void Packing_a_second_version_appends_in_descending_order()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.3",
            "--output", output.Path, "--package-name", "MyApp");
        app.With("MyApp.exe", "exe v2");
        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        string[] versions =
        [
            .. ReadFeed(output.Path)["releases"]!.AsArray().Select(r => r!["version"]!.GetValue<string>()),
        ];

        Assert.Equal(["1.2.4", "1.2.3"], versions);
    }

    [Fact]
    public void Conflicting_repack_without_force_exits_with_the_conflict_code()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");
        app.With("MyApp.exe", "exe changed");

        int code = Run(out _, out string stderr, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Equal(ExitCode.Conflict, code);
        Assert.Contains("--force", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicting_repack_with_force_succeeds()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");
        app.With("MyApp.exe", "exe changed");

        int code = Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp", "--force");

        Assert.Equal(ExitCode.Success, code);
        Assert.Single(ReadFeed(output.Path)["releases"]!.AsArray());
    }

    [Fact]
    public void Mode_and_rollout_percent_and_min_updatable_from_reach_the_feed()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path,
            "--package-name", "MyApp", "--mode", "mandatory", "--rollout-percent", "5",
            "--min-updatable-from", "1.0.0");

        JsonNode release = ReadFeed(output.Path)["releases"]!.AsArray()[0]!;
        Assert.Equal("mandatory", release["mode"]!.GetValue<string>());
        Assert.Equal(5, release["rolloutPercent"]!.GetValue<int>());
        Assert.Equal("1.0.0", release["minUpdatableFrom"]!.GetValue<string>());
    }

    [Fact]
    public void Notes_file_is_read_as_utf8()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        string notesPath = Path.Combine(output.Path, "notes.txt");
        Directory.CreateDirectory(output.Path);
        File.WriteAllText(notesPath, "修复若干问题", new System.Text.UTF8Encoding(false));

        Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path,
            "--package-name", "MyApp", "--notes-file", notesPath);

        Assert.Equal("修复若干问题",
            ReadFeed(output.Path)["releases"]!.AsArray()[0]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Notes_and_notes_file_together_are_rejected()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp", "--notes", "a", "--notes-file", "b.txt");

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--notes", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--rollout-percent", "101")]
    [InlineData("--rollout-percent", "-1")]
    [InlineData("--rollout-percent", "abc")]
    [InlineData("--mode", "forced")]
    [InlineData("--version", "not-a-version")]
    [InlineData("--min-updatable-from", "x.y")]
    [InlineData("--released-at", "yesterday")]
    [InlineData("--preserve", "*.{json,xml}")]
    public void Invalid_option_values_exit_with_the_usage_code(string option, string value)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        string version = option == "--version" ? value : "1.2.4";
        List<string> args =
        [
            "pack", "--input", app.Root, "--version", version,
            "--output", output.Path, "--package-name", "MyApp",
        ];
        if (option != "--version")
        {
            args.Add(option);
            args.Add(value);
        }

        int code = Run(out _, out string stderr, [.. args]);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--input")]
    [InlineData("--version")]
    [InlineData("--output")]
    public void Missing_required_options_exit_with_the_usage_code(string missing)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        List<string> args = ["pack"];
        if (missing != "--input") { args.AddRange(["--input", app.Root]); }
        if (missing != "--version") { args.AddRange(["--version", "1.2.4"]); }
        if (missing != "--output") { args.AddRange(["--output", output.Path]); }

        int code = Run(out _, out string stderr, [.. args]);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(missing, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_exits_with_the_usage_code()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp", "--compress-harder");

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--compress-harder", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_input_directory_exits_with_the_input_code()
    {
        using var output = new TempDirectory();
        string missing = Path.Combine(Path.GetTempPath(), "supack-missing-" + Guid.NewGuid().ToString("N"));

        int code = Run(out _, out string stderr, "pack", "--input", missing, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Equal(ExitCode.Input, code);
        Assert.Contains(missing, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_existing_feed_exits_with_the_input_code_and_does_not_overwrite_it()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        string feedPath = Path.Combine(output.Path, "releases.json");
        File.WriteAllText(feedPath, "{ broken");

        int code = Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Equal(ExitCode.Input, code);
        Assert.Equal("{ broken", File.ReadAllText(feedPath));
    }

    [Fact]
    public void Success_prints_the_package_path_and_hash()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out string stdout, out _, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Contains("MyApp-1.2.4.zip", stdout, StringComparison.Ordinal);
        Assert.Contains(
            ReadFeed(output.Path)["releases"]!.AsArray()[0]!["package"]!["sha256"]!.GetValue<string>(),
            stdout,
            StringComparison.Ordinal);
    }

    // ---- 辅助方法 ----

    private static string[] PackArgs(FakeApp app, string outputPath, params string[] extra)
        => ["pack", "--input", app.Root, "--version", "1.2.4", "--output", outputPath, "--package-name", "MyApp", .. extra];

    private static string[] FilesUnder(string root)
        => Directory.Exists(root) ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)] : [];

    /// <summary>输出目录里每个文件的路径与内容哈希：比较两次快照就能看出"一个字节都没动、也没留残渣"。</summary>
    private static Dictionary<string, string> Snapshot(string root)
        => FilesUnder(root).ToDictionary(
            file => file,
            file => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))),
            StringComparer.Ordinal);

    private static PackageManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(archive.GetEntry(".smartupdater/manifest.json")!.Open());
        return System.Text.Json.JsonSerializer.Deserialize(
            reader.ReadToEnd(), SmartUpdaterJsonContext.Default.PackageManifest)!;
    }

    // -- 冲突 / 损坏 / 不安全输入时，一个字节都不落盘 --

    [Fact]
    public void Conflict_leaves_the_output_directory_byte_for_byte_unchanged()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        Run(out _, out _, PackArgs(app, output.Path));
        Dictionary<string, string> before = Snapshot(output.Path);
        app.With("MyApp.exe", "exe changed");

        int code = Run(out _, out _, PackArgs(app, output.Path));

        Assert.Equal(ExitCode.Conflict, code);
        Assert.Equal(before, Snapshot(output.Path));
    }

    [Fact]
    public void Channel_conflict_on_a_fresh_package_directory_creates_nothing()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        string feedPath = Path.Combine(output.Path, "releases.json");
        File.WriteAllText(feedPath, """{ "schemaVersion": 1, "channel": "beta", "releases": [] }""");

        int code = Run(out _, out string stderr, PackArgs(app, output.Path));

        Assert.Equal(ExitCode.Conflict, code);
        Assert.Contains("--channel", ErrorLine(stderr), StringComparison.Ordinal);
        Assert.Equal([feedPath], FilesUnder(output.Path));
        Assert.False(Directory.Exists(Path.Combine(output.Path, "packages")));
    }

    [Fact]
    public void Malformed_existing_feed_leaves_no_package_behind()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        string feedPath = Path.Combine(output.Path, "releases.json");
        File.WriteAllText(feedPath, "{ broken");

        int code = Run(out _, out string stderr, PackArgs(app, output.Path));

        Assert.Equal(ExitCode.Input, code);
        Assert.Contains(feedPath, ErrorLine(stderr), StringComparison.Ordinal);
        Assert.Equal([feedPath], FilesUnder(output.Path));
    }

    [Fact]
    public void Unsafe_path_in_the_input_exits_with_the_input_code_and_writes_nothing()
    {
        // .suold 是更新器自己的保留后缀；从安装目录打包时确实会捡到残留的这种文件。
        using FakeApp app = FakeApp.Standard().With("legacy.dll.suold", "x");
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path));

        Assert.Equal(ExitCode.Input, code);
        Assert.Contains("legacy.dll.suold", stderr, StringComparison.Ordinal);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Fact]
    public void Empty_input_directory_exits_with_the_input_code()
    {
        using var input = new TempDirectory();
        using var output = new TempDirectory();

        int code = Run(out _, out _, "pack", "--input", input.Path, "--version", "1.2.4",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Equal(ExitCode.Input, code);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Fact]
    public void Output_path_that_is_an_existing_file_exits_with_the_unexpected_code()
    {
        using FakeApp app = FakeApp.Standard();
        using var root = new TempDirectory();
        string occupied = Path.Combine(root.Path, "releases-output");
        File.WriteAllText(occupied, "not a directory");

        int code = Run(out _, out string stderr, PackArgs(app, occupied));

        Assert.Equal(ExitCode.Unexpected, code);
        Assert.NotEmpty(ErrorLine(stderr));
        Assert.Equal("not a directory", File.ReadAllText(occupied));
    }

    [Fact]
    public void Output_path_with_a_nul_character_is_a_usage_error()
    {
        using FakeApp app = FakeApp.Standard();

        int code = Run(out _, out string stderr, PackArgs(app, "bad\0path"));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--output", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Success_leaves_no_temporary_files_and_only_the_documented_layout()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path));

        Assert.Equal(
            [Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip"), Path.Combine(output.Path, "releases.json")],
            FilesUnder(output.Path));
    }

    [Fact]
    public void Output_directory_is_created_when_missing()
    {
        using FakeApp app = FakeApp.Standard();
        using var root = new TempDirectory();
        string output = Path.Combine(root.Path, "a", "b");

        int code = Run(out _, out _, PackArgs(app, output));

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(output, "packages", "MyApp-1.2.4.zip")));
    }

    [Fact]
    public void Feed_file_is_utf8_without_bom_and_uses_lf_only()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path, "--notes", "修复若干问题"));

        byte[] bytes = File.ReadAllBytes(Path.Combine(output.Path, "releases.json"));
        Assert.Equal((byte)'{', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Contains("修复若干问题", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    // -- 幂等与已有 feed --

    [Fact]
    public void Repacking_identical_input_succeeds_without_force_and_keeps_the_first_released_at()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        Run(out _, out _, PackArgs(app, output.Path, "--released-at", "2026-09-18T10:00:00Z"));

        int code = Run(out _, out _, PackArgs(app, output.Path, "--released-at", "2026-10-01T00:00:00Z"));

        Assert.Equal(ExitCode.Success, code);
        JsonArray releases = ReadFeed(output.Path)["releases"]!.AsArray();
        Assert.Single(releases);
        Assert.Equal("2026-09-18T10:00:00Z", releases[0]!["releasedAt"]!.GetValue<string>());
    }

    [Fact]
    public void Existing_feed_extras_and_signed_older_entries_survive_a_pack()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        File.WriteAllText(Path.Combine(output.Path, "releases.json"), """
            {
              "schemaVersion": 1,
              "channel": "stable",
              "vendorNote": "内部备注",
              "releases": [
                {
                  "version": "1.2.3",
                  "releasedAt": "2026-09-17T10:00:00Z",
                  "package": { "url": "packages/MyApp-1.2.3.zip", "size": 90, "sha256": "bb" },
                  "signature": "c2ln"
                }
              ]
            }
            """);

        int code = Run(out _, out _, PackArgs(app, output.Path));

        Assert.Equal(ExitCode.Success, code);
        JsonNode feed = ReadFeed(output.Path);
        Assert.Equal("内部备注", feed["vendorNote"]!.GetValue<string>());
        JsonNode older = feed["releases"]!.AsArray().Single(r => r!["version"]!.GetValue<string>() == "1.2.3")!;
        Assert.Equal("c2ln", older["signature"]!.GetValue<string>());
    }

    [Fact]
    public void Channel_and_client_options_reach_the_feed()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path, "--channel", "beta", "--poll-interval-seconds", "300",
            "--jitter-window-seconds", "600", "--heartbeat-interval-seconds", "21600"));

        JsonNode feed = ReadFeed(output.Path);
        Assert.Equal("beta", feed["channel"]!.GetValue<string>());
        Assert.Equal(300, feed["client"]!["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Equal(600, feed["client"]!["jitterWindowSeconds"]!.GetValue<int>());
        Assert.Equal(21600, feed["client"]!["heartbeatIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void Channel_mismatch_exits_with_the_conflict_code()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        Run(out _, out _, PackArgs(app, output.Path));

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, "--channel", "beta"));

        Assert.Equal(ExitCode.Conflict, code);
        Assert.Contains("--channel", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Mixing_version_forms_exits_with_the_conflict_code_even_with_force_and_writes_nothing_more()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        Run(out _, out _, PackArgs(app, output.Path));
        Dictionary<string, string> before = Snapshot(output.Path);

        int code = Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4.0",
            "--output", output.Path, "--package-name", "MyApp", "--force");

        Assert.Equal(ExitCode.Conflict, code);
        Assert.Equal(before, Snapshot(output.Path));
    }

    // -- 选项读取：开关不带值、值选项必须有值、单值选项不可重复 --

    [Theory]
    [InlineData("--force", "x")]
    [InlineData("--force=x", null)]
    [InlineData("--force=", null)]
    public void A_switch_that_is_given_a_value_is_rejected_instead_of_being_ignored(string first, string? second)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, second is null ? [first] : [first, second]));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--force", ErrorLine(stderr), StringComparison.Ordinal);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("--notes")]
    [InlineData("--notes-file")]
    [InlineData("--channel")]
    [InlineData("--released-at")]
    [InlineData("--rollout-percent")]
    [InlineData("--min-updatable-from")]
    [InlineData("--preserve")]
    [InlineData("--poll-interval-seconds")]
    public void A_value_option_written_as_a_switch_is_rejected(string option)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, option));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Theory]
    [InlineData("--input")]
    [InlineData("--version")]
    [InlineData("--output")]
    public void A_required_option_written_as_a_switch_is_rejected(string option)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        // 该选项后面紧跟另一个选项，于是它没有值。
        string[] args = option switch
        {
            "--input" => ["pack", "--input", "--version", "1.2.4", "--output", output.Path],
            "--version" => ["pack", "--input", app.Root, "--version", "--output", output.Path],
            _ => ["pack", "--input", app.Root, "--version", "1.2.4", "--output"],
        };

        int code = Run(out _, out string stderr, args);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("--rollout-percent")]
    [InlineData("--channel")]
    [InlineData("--released-at")]
    public void A_single_valued_option_given_twice_is_rejected(string option)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, option, "1", option, "2"));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--input")]
    [InlineData("--version")]
    [InlineData("--output")]
    public void A_required_option_with_an_empty_value_is_rejected(string option)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        string[] args = option switch
        {
            "--input" => ["pack", "--input=", "--version", "1.2.4", "--output", output.Path],
            "--version" => ["pack", "--input", app.Root, "--version=", "--output", output.Path],
            _ => ["pack", "--input", app.Root, "--version", "1.2.4", "--output="],
        };

        int code = Run(out _, out string stderr, args);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--poll-interval-seconds", "-1")]
    [InlineData("--jitter-window-seconds", "abc")]
    [InlineData("--heartbeat-interval-seconds", "1.5")]
    [InlineData("--heartbeat-interval-seconds", "+5")]
    public void Invalid_client_seconds_exit_with_the_usage_code(string option, string value)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, option, value));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains(option, ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_channel_is_rejected()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, "--channel=  "));

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--channel", ErrorLine(stderr), StringComparison.Ordinal);
    }

    // -- 用法表只在"未知选项"时附带；值错误只打错误行 --

    [Fact]
    public void Value_errors_print_only_the_error_line_without_the_usage_table()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out string stderr, PackArgs(app, output.Path, "--rollout-percent", "101"));

        Assert.DoesNotContain(HelpText.PackUsage, stderr, StringComparison.Ordinal);
        Assert.Single(stderr.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void Unknown_option_prints_the_error_line_and_then_the_pack_usage_table()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out string stderr, PackArgs(app, output.Path, "--compress-harder"));

        Assert.Contains(HelpText.PackUsage, stderr, StringComparison.Ordinal);
        Assert.StartsWith("未知选项 --compress-harder", stderr, StringComparison.Ordinal);
    }

    // -- --package-name：会成为文件名与 feed 里的 URL，不许含路径分隔符等 --

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a#b")]
    [InlineData("a%b")]
    [InlineData("a?b")]
    [InlineData("a:b")]
    [InlineData("  ")]
    public void Package_name_with_unsafe_characters_is_rejected(string name)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr, "pack", "--input", app.Root, "--version", "1.2.4",
            "--output", output.Path, "--package-name", name);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--package-name", ErrorLine(stderr), StringComparison.Ordinal);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Fact]
    public void An_unusable_derived_package_name_asks_for_an_explicit_one()
    {
        using FakeApp app = new FakeApp().With("a#b.exe", "exe").With("a#b.dll", "dll");
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr,
            "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--package-name", ErrorLine(stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void Package_name_default_accepts_an_uppercase_exe_extension()
    {
        using FakeApp app = new FakeApp().With("Tool.EXE", "exe").With("Tool.dll", "dll");
        using var output = new TempDirectory();

        int code = Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path);

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(output.Path, "packages", "Tool-1.2.4.zip")));
    }

    [Fact]
    public void Package_name_default_does_not_look_into_subdirectories()
    {
        using FakeApp app = new FakeApp().With("tools/Helper.exe", "exe").With("MyApp.dll", "dll");
        using var output = new TempDirectory();

        int code = Run(out _, out string stderr,
            "pack", "--input", app.Root, "--version", "1.2.4", "--output", output.Path);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("--package-name", ErrorLine(stderr), StringComparison.Ordinal);
    }

    // -- --released-at / 版本 / 说明 / preserve --

    [Theory]
    [InlineData("2026-09-18T10:00:00", "2026-09-18T10:00:00Z")]
    [InlineData("2026-09-18T18:00:00+08:00", "2026-09-18T10:00:00Z")]
    [InlineData("2026-09-18T10:00:00.9876543Z", "2026-09-18T10:00:00Z")]
    public void Released_at_is_normalised_to_utc_seconds_regardless_of_the_machine_time_zone(string given, string expected)
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path, "--released-at", given));

        Assert.Equal(expected, ReadFeed(output.Path)["releases"]!.AsArray()[0]!["releasedAt"]!.GetValue<string>());
    }

    [Fact]
    public void Released_at_defaults_to_the_current_utc_time_with_second_precision()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Run(out _, out _, PackArgs(app, output.Path));

        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);
        string text = ReadFeed(output.Path)["releases"]!.AsArray()[0]!["releasedAt"]!.GetValue<string>();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", text);
        DateTimeOffset written = DateTimeOffset.Parse(
            text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange(written, before, after);
    }

    [Fact]
    public void Two_segment_versions_are_accepted_and_written_as_given()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        int code = Run(out _, out _, "pack", "--input", app.Root, "--version", "1.2",
            "--output", output.Path, "--package-name", "MyApp");

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(output.Path, "packages", "MyApp-1.2.zip")));
        Assert.Equal("1.2", ReadFeed(output.Path)["releases"]!.AsArray()[0]!["version"]!.GetValue<string>());
    }

    [Fact]
    public void Notes_option_is_written_verbatim_and_a_dash_led_value_works_with_equals()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path, "--notes=--例外"));

        Assert.Equal("--例外", ReadFeed(output.Path)["releases"]!.AsArray()[0]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Notes_file_bom_is_stripped()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        using var notes = new TempDirectory();
        string notesPath = Path.Combine(notes.Path, "notes.txt");
        File.WriteAllText(notesPath, "修复若干问题", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(notesPath)[0]);

        Run(out _, out _, PackArgs(app, output.Path, "--notes-file", notesPath));

        Assert.Equal("修复若干问题", ReadFeed(output.Path)["releases"]!.AsArray()[0]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_notes_file_exits_with_the_input_code()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        string missing = Path.Combine(Path.GetTempPath(), "supack-notes-" + Guid.NewGuid().ToString("N") + ".txt");

        int code = Run(out _, out string stderr, PackArgs(app, output.Path, "--notes-file", missing));

        Assert.Equal(ExitCode.Input, code);
        Assert.Contains(missing, stderr, StringComparison.Ordinal);
        Assert.Empty(FilesUnder(output.Path));
    }

    [Fact]
    public void Repeated_preserve_patterns_all_reach_the_manifest()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path, "--preserve", "appsettings.json", "--preserve", "Resources/**"));

        PackageManifest manifest = ReadManifest(Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip"));
        Assert.Equal(FilePolicy.Preserve, manifest.Files.Single(f => f.Path == "appsettings.json").Policy);
        Assert.Equal(FilePolicy.Preserve, manifest.Files.Single(f => f.Path == "Resources/logo.png").Policy);
        Assert.Equal(FilePolicy.Replace, manifest.Files.Single(f => f.Path == "MyApp.dll").Policy);
    }

    [Fact]
    public void The_written_package_can_be_opened_by_the_client_reader_and_matches_the_feed_version()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Run(out _, out _, PackArgs(app, output.Path));

        PackageManifest manifest = ReadManifest(Path.Combine(output.Path, "packages", "MyApp-1.2.4.zip"));
        Assert.Equal(new Version(1, 2, 4), manifest.Version);
    }

    // -- HelpText 与实现对账 --

    [Fact]
    public void Usage_explains_the_dash_led_value_trade_off()
        => Assert.Contains("--notes=--例外", HelpText.Usage, StringComparison.Ordinal);

    [Fact]
    public void Pack_usage_lists_exactly_the_options_the_command_knows()
    {
        string[] documented =
        [
            .. System.Text.RegularExpressions.Regex.Matches(HelpText.PackUsage, @"^ {2}--([a-z0-9-]+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value),
        ];

        Assert.Equal(PackCommand.KnownOptions.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pack_usage_states_the_defaults_and_the_version_form_boundary()
    {
        Assert.Contains("默认 stable", HelpText.PackUsage, StringComparison.Ordinal);
        Assert.Contains("默认 optional", HelpText.PackUsage, StringComparison.Ordinal);
        Assert.Contains("默认 100", HelpText.PackUsage, StringComparison.Ordinal);
        Assert.Contains("1.2.4.0", HelpText.PackUsage, StringComparison.Ordinal);
        Assert.Contains("不检查已有条目彼此之间", HelpText.PackUsage, StringComparison.Ordinal);
    }
}
