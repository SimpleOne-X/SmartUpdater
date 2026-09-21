using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>一次打包的输入。<paramref name="PreservePatterns"/> 必须是已经过 <see cref="GlobMatcher.IsValidPattern"/> 校验的模式。</summary>
internal sealed record BuildRequest(string InputDirectory, Version Version, IReadOnlyList<string> PreservePatterns);

/// <summary>一次打包的结果：包内清单、zip 全部字节，以及 zip 字节的 SHA-256（小写十六进制）。</summary>
internal sealed record BuildResult(PackageManifest Manifest, byte[] ZipBytes, string Sha256Hex);

/// <summary>
/// 把已发布的应用目录打成升级包：递归扫描目录、生成包内清单，并写出<b>逐字节可复现</b>的 zip。
/// 同一输入两次打包得到完全相同的字节（与源文件 mtime、机器时区无关），<c>pack --force</c> 的冲突判定依赖这一点。
/// </summary>
internal static class PackageBuilder
{
    /// <summary>
    /// zip 条目的固定时间戳。zip 的 DOS 时间只存墙钟数字、忽略时区，所以固定字面量即可跨时区复现；
    /// 1980-01-01 是 DOS 时间的下限（更早会抛 ArgumentOutOfRangeException），且是偶数秒（DOS 时间 2 秒粒度）。
    /// </summary>
    public static readonly DateTimeOffset FixedEntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>压缩级别写死成常量：改级别会改变全部包的字节，必须是一次显式的改动。</summary>
    private const CompressionLevel EntryCompression = CompressionLevel.Optimal;

    /// <summary>更新器自己的状态目录。整个目录不进清单也不进包。</summary>
    private const string StateDirectoryName = ".smartupdater";

    /// <summary>
    /// 用 <see cref="PackerJson.Write"/> 的设置（缩进、放宽转义，且继承 camelCase）取的清单类型信息。
    /// 走源生成的 <see cref="JsonTypeInfo{T}"/>，不用反射重载。
    /// <para>不写成 <c>new SmartUpdaterJsonContext(PackerJson.Write)</c>：构造函数会把共享的 options 封进新上下文，
    /// 此后只要 <see cref="PackerJson.Write"/> 在别处已被用过（如 feed 写出），再构造就抛 InvalidOperationException，
    /// 成败取决于静态初始化顺序。<see cref="JsonSerializerOptions.GetTypeInfo"/> 只读 options，没有这个隐患。</para>
    /// </summary>
    private static readonly JsonTypeInfo<PackageManifest> ManifestTypeInfo
        = (JsonTypeInfo<PackageManifest>)PackerJson.Write.GetTypeInfo(typeof(PackageManifest));

    /// <exception cref="DirectoryNotFoundException">输入目录不存在。</exception>
    /// <exception cref="InvalidOperationException">输入目录里没有可打包的文件，或含不安全的路径。</exception>
    public static BuildResult Build(BuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string root = request.InputDirectory;
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"输入目录不存在：{root}");
        }

        List<SourceFile> sources = Scan(root);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"输入目录里没有可打包的文件（{StateDirectoryName}/ 目录不算）：{root}");
        }

        // 每个源文件只读一次：哈希、Size 与写进 zip 的字节出自同一份内存，扫描期间文件被改也不会出现"清单与包内容对不上"。
        // 峰值内存 ≈ zip 本身 + 最大的单个文件。
        var files = new List<ManifestFile>(sources.Count);
        using var zipStream = new MemoryStream();
        PackageManifest manifest;
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (SourceFile source in sources)
            {
                byte[] content = File.ReadAllBytes(source.FullPath);
                files.Add(new ManifestFile
                {
                    Path = source.ManifestPath,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                    Size = content.Length,
                    Policy = IsPreserved(request.PreservePatterns, source.ManifestPath) ? FilePolicy.Preserve : FilePolicy.Replace,
                });
                WriteEntry(zip, source.ManifestPath, content);
            }

            // 校验的是即将写进清单的那份 ManifestFile 列表，与客户端应用时校验的对象一致。
            // 不合格就抛，这里之前写进 zip 的内容随 MemoryStream 一起丢弃，不会有半成品落盘。
            EnsureManifestPathsAreValid(files);

            manifest = new PackageManifest
            {
                SchemaVersion = PackageReader.SupportedSchemaVersion,
                Version = request.Version,
                Files = files,
            };

            // 清单条目必须显式写在最后：它的名字以 . 开头，ordinal 序本来会把它排在字母前面。
            WriteEntry(zip, PackageReader.ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestTypeInfo));
        }

        // ZipArchive 必须先释放（写出中央目录）再取字节。
        byte[] zipBytes = zipStream.ToArray();
        return new BuildResult(manifest, zipBytes, Convert.ToHexStringLower(SHA256.HashData(zipBytes)));
    }

    /// <summary>递归扫描，返回排除状态目录后的文件，按清单路径 ordinal 升序。</summary>
    private static List<SourceFile> Scan(string root)
    {
        var sources = new List<SourceFile>();
        foreach (string fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string manifestPath = ToManifestPath(Path.GetRelativePath(root, fullPath));
            if (!IsInStateDirectory(manifestPath))
            {
                sources.Add(new SourceFile(manifestPath, fullPath));
            }
        }

        sources.Sort((a, b) => string.CompareOrdinal(a.ManifestPath, b.ManifestPath));
        return sources;
    }

    /// <summary>
    /// 只把本平台的目录分隔符换成 /：Windows 上 \ 是分隔符，Linux 上 \ 是文件名里的普通字符——
    /// 后者不能悄悄改成目录层级，留给路径校验器以"含反斜杠"拒绝。
    /// </summary>
    private static string ToManifestPath(string relativePath)
        => Path.DirectorySeparatorChar == '/'
            ? relativePath
            : relativePath.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>按路径段比较，不用子串包含：<c>my.smartupdater.config</c> 是应用自己的文件，必须进包。</summary>
    private static bool IsInStateDirectory(string manifestPath)
    {
        int slash = manifestPath.IndexOf('/', StringComparison.Ordinal);
        string firstSegment = slash < 0 ? manifestPath : manifestPath[..slash];
        return string.Equals(firstSegment, StateDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 整份文件列表过客户端的路径校验器（zip-slip 防线），一次报全而不是逐条往返。
    /// 用的是客户端 <see cref="PackageReader"/> 在应用时调用的同一个
    /// <see cref="ManifestPathValidator.Validate"/>，而不是逐条的 <c>IsSafeRelativePath</c>：
    /// 前者还会拒绝"不分大小写的重复路径"。大小写敏感的文件系统（Linux / WSL）上
    /// <c>Docs/a.txt</c> 与 <c>docs/A.txt</c> 可以并存，若 Packer 放行，每个客户端都会在应用时拒包。
    /// 规则只在校验器里一份，Packer 不复制。
    /// <para>注意：Windows 会在 Win32 层规范化掉不少非法名字，真实目录扫描下可达的主要是
    /// 保留后缀（.suold / .sunew）与保留设备名等；这里是纵深防御，不是主防线。
    /// 单独成方法是为了能直接喂路径列表测试（NTFS 上建不出只差大小写的两个文件）。</para>
    /// </summary>
    internal static void EnsureManifestPathsAreValid(IReadOnlyList<ManifestFile> files)
    {
        IReadOnlyList<string> problems = ManifestPathValidator.Validate(files);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "输入目录里有不能写进升级包的路径：" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
        }
    }

    private static bool IsPreserved(IReadOnlyList<string> patterns, string manifestPath)
    {
        foreach (string pattern in patterns)
        {
            if (GlobMatcher.Matches(pattern, manifestPath))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteEntry(ZipArchive zip, string entryName, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, EntryCompression);

        // 必须在 Open() 之前设：写过之后 .NET 会拒绝再改时间戳。
        entry.LastWriteTime = FixedEntryTimestamp;

        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private sealed record SourceFile(string ManifestPath, string FullPath);
}
