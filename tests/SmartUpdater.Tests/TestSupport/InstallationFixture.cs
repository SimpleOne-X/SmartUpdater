using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>
/// 假"已安装应用"：安装目录 + .smartupdater/manifest.json + state.json + %LOCALAPPDATA% 替身，全部在一个临时目录里。
/// 用法：<c>using InstallationFixture f = InstallationFixture.StandardV1();</c>，再用 <see cref="StagePackage"/> 把包放进下载缓存，
/// 交给 <see cref="PackageApplier"/> / 恢复逻辑；最后用 <see cref="AssertStandardV1"/> / <see cref="AssertStandardV110Files"/> / <see cref="Snapshot"/> 断言磁盘。
/// </summary>
internal sealed class InstallationFixture : IDisposable
{
    public const string AppId = "TestApp-0badf00d";

    public static readonly Guid DeviceGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>空的安装：安装目录、状态目录、下载缓存目录都在，但没有任何文件（首次安装场景）。</summary>
    public InstallationFixture()
    {
        Temp = new TempDirectory();
        Directory.CreateDirectory(Temp.Resolve("app"));
        Directory.CreateDirectory(Temp.Resolve("appdata"));
        Layout = new UpdateLayout(Temp.Resolve("app"), Temp.Resolve("appdata"), AppId);
        Directory.CreateDirectory(Layout.StateDirectory);
        Directory.CreateDirectory(Layout.DownloadCacheDirectory);
    }

    /// <summary>整个假环境的根（安装目录在 <c>app</c>，LocalAppData 替身在 <c>appdata</c>）。</summary>
    public TempDirectory Temp { get; }

    /// <summary>指向假环境的布局，产品代码一律通过它拿路径。</summary>
    public UpdateLayout Layout { get; }

    /// <summary>标准 v1.0.0：app.exe=exe-v1、keep.dll=same、old.dll=old、settings.json=user（preserve）；state 1.0.0。</summary>
    public static InstallationFixture StandardV1()
    {
        var fixture = new InstallationFixture();
        fixture.WithInstalledFiles(
            new Version(1, 0, 0),
            ("app.exe", "exe-v1", FilePolicy.Replace),
            ("keep.dll", "same", FilePolicy.Replace),
            ("old.dll", "old", FilePolicy.Replace),
            ("settings.json", "user", FilePolicy.Preserve));
        fixture.WithState(new Version(1, 0, 0));
        return fixture;
    }

    /// <summary>标准 v1.1.0 包：替换 app.exe、新增 new.dll、删除 old.dll、settings.json 为 preserve（已存在 → 跳过）。</summary>
    public static TestPackage StandardV110() => new TestPackage { Version = new Version(1, 1, 0) }
        .Add("app.exe", "exe-v2")
        .Add("keep.dll", "same")
        .Add("new.dll", "new")
        .Add("settings.json", "default", FilePolicy.Preserve);

    /// <summary>以 app.exe 为主程序的标准请求，当前版本默认 1.0.0。</summary>
    public static ApplyRequest StandardRequest(string packagePath, Version? current = null)
        => new(packagePath, current ?? new Version(1, 0, 0), "app.exe");

    /// <summary>在安装目录下写一个文本文件（UTF-8 无 BOM，自动建父目录），可用来种下 .suold / .sunew 等任意残留。</summary>
    public InstallationFixture WithFile(string relativePath, string content)
    {
        WriteInstall(relativePath, content);
        return this;
    }

    /// <summary>在安装目录下写一个二进制文件（自动建父目录），用来造截断 / 零长度的 .sunew 之类的撕裂写残留。</summary>
    public InstallationFixture WithBytes(string relativePath, byte[] content)
    {
        string full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return this;
    }

    /// <summary>写入一组已安装文件，并据此生成 <c>.smartupdater/manifest.json</c>（hash、size 与文件内容一致）。</summary>
    public InstallationFixture WithInstalledFiles(Version version, params (string Path, string Content, FilePolicy Policy)[] files)
    {
        foreach ((string path, string content, _) in files)
        {
            WriteInstall(path, content);
        }

        var manifest = new PackageManifest
        {
            SchemaVersion = 1,
            Version = version,
            Files = [.. files.Select(f => new ManifestFile
            {
                Path = f.Path,
                Sha256 = TestPackage.Sha256Hex(f.Content),
                Size = Utf8NoBom.GetByteCount(f.Content),
                Policy = f.Policy,
            })],
        };
        File.WriteAllBytes(Layout.ManifestFile, JsonSerializer.SerializeToUtf8Bytes(manifest, SmartUpdaterJsonContext.Default.PackageManifest));
        return this;
    }

    /// <summary>写 state.json：当前版本 + 固定的 <see cref="DeviceGuid"/>。</summary>
    public InstallationFixture WithState(Version? current)
    {
        var state = new UpdateState { CurrentVersion = current, DeviceGuid = DeviceGuid };
        File.WriteAllBytes(Layout.StateFile, JsonSerializer.SerializeToUtf8Bytes(state, SmartUpdaterJsonContext.Default.UpdateState));
        return this;
    }

    /// <summary>把包放进下载缓存目录（%LOCALAPPDATA%\AppId\updates\&lt;version&gt;.zip），返回完整路径。</summary>
    public string StagePackage(TestPackage package) => package.Save(Layout.GetDownloadPath(package.Version));

    /// <summary>相对路径（正斜杠）→ 安装目录下的完整路径。</summary>
    public string Resolve(string relativePath) => Layout.ResolveInstallPath(relativePath);

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public string Read(string relativePath) => File.ReadAllText(Resolve(relativePath), Utf8NoBom);

    /// <summary>安装目录里的本地清单；不存在返回 null。</summary>
    public PackageManifest? ReadManifest()
        => File.Exists(Layout.ManifestFile)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(Layout.ManifestFile), SmartUpdaterJsonContext.Default.PackageManifest)
            : null;

    public UpdateState ReadState()
        => JsonSerializer.Deserialize(File.ReadAllBytes(Layout.StateFile), SmartUpdaterJsonContext.Default.UpdateState)!;

    public JournalReadResult ReadJournal()
        => new JournalStore(Layout.JournalFile, PhysicalFileOperations.Instance, NullUpdateLog.Instance).Read();

    /// <summary>安装目录下全部 .sunew / .suold（相对路径，正斜杠，Ordinal 排序）。</summary>
    public IReadOnlyList<string> Leftovers()
        => [.. Directory.EnumerateFiles(Layout.InstallDirectory, "*", SearchOption.AllDirectories)
            .Where(p => SwapFileNames.HasSwapSuffix(p))
            .Select(RelativeOf)
            .Order(StringComparer.Ordinal)];

    /// <summary>安装目录（含 .smartupdater）全部文件的 "相对路径=sha256"，用于断言"零副作用"。</summary>
    public IReadOnlyList<string> Snapshot()
        => [.. Directory.EnumerateFiles(Layout.InstallDirectory, "*", SearchOption.AllDirectories)
            .Select(p => $"{RelativeOf(p)}={Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))}")
            .Order(StringComparer.Ordinal)];

    /// <summary>下载缓存目录里的文件名（Ordinal 排序）。</summary>
    public IReadOnlyList<string> CacheFiles()
        => Directory.Exists(Layout.DownloadCacheDirectory)
            ? [.. Directory.EnumerateFiles(Layout.DownloadCacheDirectory).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>断言磁盘是完整的标准 v1.0.0（回滚 / 未开始的终态）。</summary>
    public void AssertStandardV1()
    {
        Assert.Equal("exe-v1", Read("app.exe"));
        Assert.Equal("same", Read("keep.dll"));
        Assert.Equal("old", Read("old.dll"));
        Assert.Equal("user", Read("settings.json"));
        Assert.False(Exists("new.dll"));
        Assert.Equal(new Version(1, 0, 0), ReadManifest()!.Version);
        Assert.Equal(4, ReadManifest()!.Files.Count);
        Assert.Equal(new Version(1, 0, 0), ReadState().CurrentVersion);
    }

    /// <summary>断言文件已是标准 v1.1.0（不检查 .suold / state / journal，它们随阶段不同）。</summary>
    public void AssertStandardV110Files()
    {
        Assert.Equal("exe-v2", Read("app.exe"));
        Assert.Equal("same", Read("keep.dll"));
        Assert.Equal("new", Read("new.dll"));
        Assert.Equal("user", Read("settings.json"));
        Assert.False(Exists("old.dll"));
        PackageManifest manifest = ReadManifest()!;
        Assert.Equal(new Version(1, 1, 0), manifest.Version);
        Assert.Equal(new[] { "app.exe", "keep.dll", "new.dll", "settings.json" }, manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
    }

    public void Dispose() => Temp.Dispose();

    private string RelativeOf(string fullPath)
        => Path.GetRelativePath(Layout.InstallDirectory, fullPath).Replace('\\', '/');

    private void WriteInstall(string relativePath, string content)
    {
        string full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Utf8NoBom);
    }
}
