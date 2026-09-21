using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class RoundTripTests
{
    private const string AppId = "RoundTrip-0badf00d";

    private static void Pack(TempDirectory output, FakeApp app, string version, params string[] extra)
    {
        List<string> args =
        [
            "pack", "--input", app.Root, "--version", version, "--output", output.Path,
            "--package-name", "MyApp", "--released-at", "2026-09-18T10:00:00Z",
            .. extra,
        ];

        Assert.True(ArgumentParser.TryParse([.. args], out ParsedCommandLine? parsed, out string? parseError), parseError);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = PackCommand.Run(parsed!, stdout, stderr);
        Assert.True(code == ExitCode.Success, $"pack 退出码 {code}：{stderr}");
        Assert.Empty(stderr.ToString());
    }

    /// <summary>
    /// 客户端库的包应用器。对它的全部依赖都收敛在这一个方法里：
    /// 若应用器的签名有调整，只改这里。
    /// </summary>
    private static ApplyResult ApplyPackage(string packagePath, string installDirectory, string localAppData, Version? currentVersion)
    {
        var layout = new UpdateLayout(installDirectory, localAppData, AppId);
        var applier = new PackageApplier(
            layout, PhysicalFileOperations.Instance, NullUpdateLog.Instance, TimeProvider.System);

        return applier.Apply(
            new ApplyRequest(packagePath, currentVersion, "MyApp.exe"),
            progress: null,
            TestContext.Current.CancellationToken);
    }

    /// <summary>把一个目录读成"相对路径 → 内容 SHA-256"的字典，排除 .smartupdater/。</summary>
    private static SortedDictionary<string, string> HashTree(string root)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".smartupdater/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tree[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return tree;
    }

    private static string PackagePath(TempDirectory output, string version)
        => Path.Combine(output.Path, "packages", $"MyApp-{version}.zip");

    // ========== 场景 1：首次安装 ==========

    [Fact]
    public void Packed_package_applies_to_an_empty_directory_and_matches_the_input_byte_for_byte()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");

        ApplyResult result = ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, currentVersion: null);

        Assert.Equal(new Version(1, 0, 0), result.ToVersion);
        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    [Fact]
    public void Applying_twice_is_idempotent()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);

        // 第二次应用同一个包：结果必须仍然与输入一致。
        CleanUpSwapFiles(install.Path);
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, new Version(1, 0, 0));

        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    // ========== 场景 2：升级（改一个、删一个、加一个、preserve 一个） ==========

    [Fact]
    public void Upgrade_replaces_adds_deletes_and_preserves()
    {
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        // v1：四个文件，appsettings.json 标 preserve。
        using (FakeApp v1 = FakeApp.Standard())
        {
            Pack(output, v1, "1.0.0", "--preserve", "appsettings.json");
            ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);
            Assert.Equal(HashTree(v1.Root), HashTree(install.Path));
        }

        // 用户改了配置文件 —— preserve 的意义就在这里。
        string settingsPath = Path.Combine(install.Path, "appsettings.json");
        const string UserEdited = """{"setting":999,"userAdded":true}""";
        File.WriteAllText(settingsPath, UserEdited);
        CleanUpSwapFiles(install.Path);

        // v2：改 MyApp.exe、删 MyApp.dll、加 New.dll、appsettings.json 仍 preserve。
        using FakeApp v2 = new FakeApp()
            .With("MyApp.exe", "exe v2")
            .With("New.dll", "new dll")
            .With("appsettings.json", """{"setting":2}""")
            .With("Resources/logo.png", "png v1");

        Pack(output, v2, "1.1.0", "--preserve", "appsettings.json");
        ApplyResult result = ApplyPackage(
            PackagePath(output, "1.1.0"), install.Path, appData.Path, new Version(1, 0, 0));

        Assert.Equal(new Version(1, 0, 0), result.FromVersion);
        Assert.Equal(new Version(1, 1, 0), result.ToVersion);

        SortedDictionary<string, string> installed = HashTree(install.Path);

        // 改的生效了
        Assert.Equal(FakeApp.Sha256Hex("exe v2"), installed["MyApp.exe"]);
        // 加的出现了
        Assert.Equal(FakeApp.Sha256Hex("new dll"), installed["New.dll"]);
        // 删的没了
        Assert.False(installed.ContainsKey("MyApp.dll"));
        // 没动的还在
        Assert.Equal(FakeApp.Sha256Hex("png v1"), installed["Resources/logo.png"]);
        // preserve 的保住了用户的改动
        Assert.Equal(UserEdited, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void A_preserve_file_dropped_from_the_new_manifest_is_still_not_deleted()
    {
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        using (FakeApp v1 = FakeApp.Standard())
        {
            Pack(output, v1, "1.0.0", "--preserve", "appsettings.json");
            ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);
        }

        const string UserEdited = """{"setting":999}""";
        File.WriteAllText(Path.Combine(install.Path, "appsettings.json"), UserEdited);
        CleanUpSwapFiles(install.Path);

        // v2 完全不再列出 appsettings.json —— 也不删。
        using FakeApp v2 = new FakeApp()
            .With("MyApp.exe", "exe v2")
            .With("MyApp.dll", "dll v1")
            .With("Resources/logo.png", "png v1");

        Pack(output, v2, "1.1.0");
        ApplyPackage(PackagePath(output, "1.1.0"), install.Path, appData.Path, new Version(1, 0, 0));

        Assert.True(File.Exists(Path.Combine(install.Path, "appsettings.json")));
        Assert.Equal(UserEdited, File.ReadAllText(Path.Combine(install.Path, "appsettings.json")));
    }

    [Fact]
    public void A_manually_deleted_file_is_restored_by_applying_the_same_version()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);
        CleanUpSwapFiles(install.Path);

        // 手工删掉一个文件（模拟杀软误删），再应用一次同版本的全量包。
        File.Delete(Path.Combine(install.Path, "MyApp.dll"));
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, new Version(1, 0, 0));

        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    [Fact]
    public void Non_ascii_paths_survive_the_round_trip()
    {
        using FakeApp app = FakeApp.Standard().With("资源/图标.png", "icon").With("说明.txt", "说明内容");
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);

        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    [Fact]
    public void Nested_directories_and_empty_files_survive_the_round_trip()
    {
        using FakeApp app = FakeApp.Standard()
            .With("a/b/c/deep.txt", "deep")
            .WithBytes("empty.dat", []);
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);

        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    // ========== 场景 3：签名往返 ==========

    [Fact]
    public void Packed_and_signed_feed_verifies_and_breaks_when_tampered()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();

        Pack(output, app, "1.0.0");

        string feedPath = Path.Combine(output.Path, "releases.json");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyPath = Path.Combine(output.Path, "private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        Assert.True(ArgumentParser.TryParse(
            ["sign", "--feed", feedPath, "--key", keyPath], out ParsedCommandLine? parsed, out string? parseError),
            parseError);
        var stdout = new StringWriter();
        Assert.Equal(ExitCode.Success, SignCommand.Run(parsed!, stdout, new StringWriter()));

        // 用客户端的验签路径（公钥从打印出来的 base64 导入）验证。
        string publicKey = ReleaseSignature.ExportPublicKey(key);
        Assert.Contains(publicKey, stdout.ToString(), StringComparison.Ordinal);

        ReleaseFeedDocument Read() => JsonSerializer.Deserialize(
            File.ReadAllText(feedPath), SmartUpdaterJsonContext.Default.ReleaseFeedDocument)!;

        using (ECDsa imported = ReleaseSignature.ImportPublicKey(publicKey))
        {
            Assert.True(ReleaseSignature.Verify(Read().Releases[0], imported));
        }

        // 篡改 package.sha256 —— 攻击者最想改的那个字段。
        JsonNode feed = JsonNode.Parse(File.ReadAllText(feedPath))!;
        feed["releases"]!.AsArray()[0]!["package"]!["sha256"] =
            "0000000000000000000000000000000000000000000000000000000000000000";
        File.WriteAllText(feedPath, feed.ToJsonString(PackerJson.Write));

        using (ECDsa imported = ReleaseSignature.ImportPublicKey(publicKey))
        {
            Assert.False(ReleaseSignature.Verify(Read().Releases[0], imported));
        }
    }

    [Fact]
    public void Signing_does_not_disturb_the_package_the_feed_points_at()
    {
        using FakeApp app = FakeApp.Standard();
        using var output = new TempDirectory();
        using var install = new TempDirectory();
        using var appData = new TempDirectory();

        Pack(output, app, "1.0.0");
        byte[] before = File.ReadAllBytes(PackagePath(output, "1.0.0"));

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyPath = Path.Combine(output.Path, "private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        Assert.True(ArgumentParser.TryParse(
            ["sign", "--feed", Path.Combine(output.Path, "releases.json"), "--key", keyPath],
            out ParsedCommandLine? parsed, out _));
        SignCommand.Run(parsed!, new StringWriter(), new StringWriter());

        // 签名只碰 feed，包一个字节都不该动，而且签完之后包仍然装得上。
        Assert.Equal(before, File.ReadAllBytes(PackagePath(output, "1.0.0")));
        ApplyPackage(PackagePath(output, "1.0.0"), install.Path, appData.Path, null);
        Assert.Equal(HashTree(app.Root), HashTree(install.Path));
    }

    // ========== 辅助 ==========

    /// <summary>
    /// 应用器把 .suold 留到"新版本成功启动后"才删，而本测试不启动进程。
    /// 这里手工清掉它们，模拟新版本启动后的清理，好让下一轮应用从干净状态开始。
    /// </summary>
    private static void CleanUpSwapFiles(string installDirectory)
    {
        foreach (string file in Directory.GetFiles(installDirectory, "*.suold", SearchOption.AllDirectories))
        {
            DeleteWithRetry(file);
        }

        foreach (string file in Directory.GetFiles(installDirectory, "*.sunew", SearchOption.AllDirectories))
        {
            DeleteWithRetry(file);
        }

        string journal = Path.Combine(installDirectory, ".smartupdater", "journal.json");
        if (File.Exists(journal))
        {
            DeleteWithRetry(journal);
        }
    }

    /// <summary>
    /// 杀软 / 索引器会在文件刚写完时短暂占用它，导致 Delete 偶发共享冲突（实测 journal.json）。
    /// 有界重试：多次仍失败就照常抛出，不吞异常。
    /// </summary>
    private static void DeleteWithRetry(string path)
    {
        const int MaxAttempts = 10;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                SpinWait.SpinUntil(static () => false, millisecondsTimeout: 50);
            }
        }
    }
}
