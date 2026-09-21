using System.IO.Compression;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>升级包的格式有问题：不是 zip、缺 manifest、manifest 不合法、路径不安全或条目与 manifest 对不上。</summary>
internal sealed class PackageFormatException : Exception
{
    public PackageFormatException(string message)
        : base(message)
    {
    }

    public PackageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>已通过全部校验的升级包。持有底层 zip 句柄，用完必须释放。</summary>
internal sealed class PackageContents : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private bool _disposed;

    internal PackageContents(ZipArchive archive, PackageManifest manifest, Dictionary<string, ZipArchiveEntry> entries, long totalFileBytes)
    {
        _archive = archive;
        _entries = entries;
        Manifest = manifest;
        TotalFileBytes = totalFileBytes;
    }

    /// <summary>包内的 manifest。</summary>
    public PackageManifest Manifest { get; }

    /// <summary>manifest 列出的全部文件（已校验）。</summary>
    public IReadOnlyList<ManifestFile> Files => Manifest.Files;

    /// <summary>全部文件的 <see cref="ManifestFile.Size"/> 之和。</summary>
    public long TotalFileBytes { get; }

    /// <summary>按 manifest 路径（<see cref="StringComparison.Ordinal"/> 精确匹配）打开 zip 条目。返回的流不可 seek，调用方边读边算 SHA-256。</summary>
    /// <exception cref="InvalidOperationException">路径不在 manifest 里。校验通过后不可能发生，属编程错误。</exception>
    public Stream OpenEntry(string manifestPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifestPath);

        if (!_entries.TryGetValue(manifestPath, out ZipArchiveEntry? entry))
        {
            throw new InvalidOperationException($"升级包内没有 manifest 路径 '{manifestPath}' 对应的条目。");
        }

        return entry.Open();
    }

    /// <summary>释放底层 zip 句柄。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
    }
}

/// <summary>
/// 打开并校验升级包。zip-slip 防线：本类从不把 zip 条目名当路径用，条目名只用来与已校验的 manifest 路径做对应；
/// 后续写盘只使用 manifest 路径。
/// </summary>
internal static class PackageReader
{
    /// <summary>包内 manifest 的条目名。</summary>
    public const string ManifestEntryName = ".smartupdater/manifest.json";

    /// <summary>本客户端认识的 manifest 协议版本。</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>manifest 解压后的字节数上限。manifest 只是文件清单，正常远小于此；更大的视为解压炸弹。</summary>
    internal const int MaxManifestBytes = 16 * 1024 * 1024;

    /// <summary>打开并校验升级包。任何格式问题都抛 <see cref="PackageFormatException"/>（原异常作 InnerException），并且不留下打开的句柄。</summary>
    public static PackageContents Open(string packagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);

        ZipArchive archive = OpenArchive(packagePath);
        try
        {
            return Load(archive);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            archive.Dispose();
            throw new PackageFormatException($"升级包已损坏：{packagePath}", ex);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static ZipArchive OpenArchive(string packagePath)
    {
        try
        {
            return ZipFile.OpenRead(packagePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PackageFormatException($"无法打开升级包：{packagePath}", ex);
        }
    }

    private static PackageContents Load(ZipArchive archive)
    {
        PackageManifest manifest = ReadManifest(archive);

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new PackageFormatException($"不认识的 schemaVersion {manifest.SchemaVersion}，本客户端只支持 {SupportedSchemaVersion}。");
        }

        // 源生成器不尊重可空注解：JSON 里的 null 会穿过 required / 默认值，这里必须显式拒绝。
        if (manifest.Version is null)
        {
            throw new PackageFormatException("manifest 缺少 version。");
        }

        if (manifest.Files is null)
        {
            throw new PackageFormatException("manifest 缺少 files。");
        }

        IReadOnlyList<ManifestFile> files = manifest.Files;
        long totalBytes = 0;
        foreach (ManifestFile file in files)
        {
            if (file is null)
            {
                throw new PackageFormatException("manifest 的 files 里含空条目（JSON null）。");
            }

            if (!IsHexSha256(file.Sha256))
            {
                throw new PackageFormatException($"文件 '{file.Path}' 的 sha256 不是 64 位小写十六进制：'{file.Sha256}'。");
            }

            if (file.Size < 0)
            {
                throw new PackageFormatException($"文件 '{file.Path}' 的 size 为负数：{file.Size}。");
            }

            // 各文件 size 之和是磁盘预检的依据，绝不能回绕成一个很小甚至为负的数。
            if (file.Size > long.MaxValue - totalBytes)
            {
                throw new PackageFormatException($"manifest 的 size 总和超出 long 范围（累加到文件 '{file.Path}' 时溢出）。");
            }

            totalBytes += file.Size;
        }

        // 路径校验必须先于条目对应：否则不安全路径只会被报成"多余条目"。
        IReadOnlyList<string> pathErrors = ManifestPathValidator.Validate(files);
        if (pathErrors.Count > 0)
        {
            throw new PackageFormatException($"manifest 含不安全的路径：{string.Join("; ", pathErrors)}");
        }

        // 目录条目与 manifest 条目本身不参与对应。同名条目一律拒绝：否则"校验的是一个、读到的是另一个"就是可行的攻击。
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            if (candidate.FullName.EndsWith('/') || candidate.FullName == ManifestEntryName)
            {
                continue;
            }

            if (!entries.TryAdd(candidate.FullName, candidate))
            {
                throw new PackageFormatException($"升级包内存在重复的条目名：{candidate.FullName}");
            }
        }

        foreach (ManifestFile file in files)
        {
            if (!entries.TryGetValue(file.Path, out ZipArchiveEntry? entry))
            {
                throw new PackageFormatException($"manifest 列出的文件在包内不存在：{file.Path}");
            }

            if (entry.Length != file.Size)
            {
                throw new PackageFormatException($"文件 {file.Path} 的 size 与包内条目不符（manifest {file.Size}，条目 {entry.Length}）。");
            }
        }

        var listed = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        string[] unlisted = [.. entries.Keys.Where(name => !listed.Contains(name))];
        if (unlisted.Length > 0)
        {
            throw new PackageFormatException($"包内存在 manifest 未列出的条目：{string.Join(", ", unlisted)}");
        }

        return new PackageContents(archive, manifest, entries, totalBytes);
    }

    private static PackageManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry? manifestEntry = null;
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            if (candidate.FullName != ManifestEntryName)
            {
                continue;
            }

            if (manifestEntry is not null)
            {
                throw new PackageFormatException($"升级包内存在重复的 {ManifestEntryName} 条目。");
            }

            manifestEntry = candidate;
        }

        if (manifestEntry is null)
        {
            throw new PackageFormatException($"升级包缺少 {ManifestEntryName}。");
        }

        using MemoryStream buffer = ReadManifestBytes(manifestEntry);
        try
        {
            return JsonSerializer.Deserialize(buffer, SmartUpdaterJsonContext.Default.PackageManifest)
                ?? throw new PackageFormatException("manifest 内容为空（JSON null）。");
        }
        catch (JsonException ex)
        {
            throw new PackageFormatException($"manifest 无法解析：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 把 manifest 解压进内存，超过 <see cref="MaxManifestBytes"/> 立即放弃。不信任头部声明的大小：压缩率极高的条目可以声明很小、解出很大。
    /// </summary>
    private static MemoryStream ReadManifestBytes(ZipArchiveEntry manifestEntry)
    {
        var buffer = new MemoryStream();
        try
        {
            using Stream stream = manifestEntry.Open();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxManifestBytes)
                {
                    throw new PackageFormatException($"manifest 解压后超过 {MaxManifestBytes} 字节上限，拒绝读取。");
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static bool IsHexSha256(string? value)
        => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
