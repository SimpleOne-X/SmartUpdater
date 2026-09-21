using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>流式构造测试用升级包：任意文件 + 自动生成的 .smartupdater/manifest.json。</summary>
internal sealed class TestPackage
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly List<(string Path, byte[] Content, FilePolicy Policy)> _files = [];

    public Version Version { get; init; } = new(1, 1, 0);

    public int SchemaVersion { get; init; } = 1;

    public TestPackage Add(string path, string content, FilePolicy policy = FilePolicy.Replace)
        => AddBytes(path, Utf8NoBom.GetBytes(content), policy);

    public TestPackage AddBytes(string path, byte[] content, FilePolicy policy = FilePolicy.Replace)
    {
        _files.Add((path, content, policy));
        return this;
    }

    public static string Sha256Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Sha256Hex(string content) => Sha256Hex(Utf8NoBom.GetBytes(content));

    public PackageManifest BuildManifest() => new()
    {
        SchemaVersion = SchemaVersion,
        Version = Version,
        Files = [.. _files.Select(f => new ManifestFile
        {
            Path = f.Path,
            Sha256 = Sha256Hex(f.Content),
            Size = f.Content.Length,
            Policy = f.Policy,
        })],
    };

    /// <param name="zipPath">输出路径。</param>
    /// <param name="customize">在写完常规条目后对 zip 做额外改动（加多余条目、目录条目等）。</param>
    /// <param name="manifestJsonOverride">为 null 时写 <see cref="BuildManifest"/> 的序列化结果；否则原样写入这段文本。</param>
    /// <param name="omitManifest">true 时不写 manifest 条目。</param>
    public string Save(string zipPath, Action<ZipArchive>? customize = null, string? manifestJsonOverride = null, bool omitManifest = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // Update 模式才支持 customize 里的 GetEntry / Delete（Create 模式下 Entries 与 GetEntry 抛 NotSupportedException）
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);

        foreach ((string path, byte[] content, _) in _files)
        {
            using Stream entry = archive.CreateEntry(path).Open();
            entry.Write(content);
        }

        if (!omitManifest)
        {
            string json = manifestJsonOverride ?? JsonSerializer.Serialize(BuildManifest(), SmartUpdaterJsonContext.Default.PackageManifest);
            using Stream entry = archive.CreateEntry(PackageReader.ManifestEntryName).Open();
            entry.Write(Utf8NoBom.GetBytes(json));
        }

        customize?.Invoke(archive);
        return zipPath;
    }
}
