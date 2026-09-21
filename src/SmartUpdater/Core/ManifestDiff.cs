namespace SimpleOneX.SmartUpdater;

/// <summary>清单比对的结果。</summary>
/// <param name="Writes">需要写入或覆盖的文件。</param>
/// <param name="Deletes">需要删除的文件路径。</param>
/// <param name="Skips">因 <see cref="FilePolicy.Preserve"/> 且目标已存在而跳过的文件。</param>
internal sealed record ManifestDiffResult(
    IReadOnlyList<ManifestFile> Writes,
    IReadOnlyList<string> Deletes,
    IReadOnlyList<ManifestFile> Skips);

/// <summary>按路径做字典比对，算出写入、删除、跳过三个集合。</summary>
internal static class ManifestDiff
{
    private const string StateDirectoryPrefix = ".smartupdater/";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>比对本地清单与新清单。</summary>
    /// <param name="localManifest">当前已安装版本的清单。首次安装时为 null。</param>
    /// <param name="incomingManifest">升级包内的清单。</param>
    /// <param name="targetExists">判断安装目录下某个相对路径是否已存在。</param>
    public static ManifestDiffResult Compute(
        PackageManifest? localManifest,
        PackageManifest incomingManifest,
        Func<string, bool> targetExists)
    {
        ArgumentNullException.ThrowIfNull(incomingManifest);
        ArgumentNullException.ThrowIfNull(targetExists);

        Dictionary<string, ManifestFile> local = Index(localManifest);
        Dictionary<string, ManifestFile> incoming = Index(incomingManifest);

        var writes = new List<ManifestFile>();
        var skips = new List<ManifestFile>();

        foreach (ManifestFile file in incoming.Values)
        {
            if (file.Policy == FilePolicy.Preserve && targetExists(file.Path))
            {
                // preserve 且目标已存在：保留使用者现有文件，不覆盖
                skips.Add(file);
                continue;
            }

            // 哈希相同但目标文件已不存在时也要重写（自愈；对 preserve 就是"目标不存在时写入"），即末尾的 targetExists 子句
            bool unchanged = local.TryGetValue(file.Path, out ManifestFile? existing)
                && string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && targetExists(file.Path);

            if (!unchanged)
            {
                writes.Add(file);
            }
        }

        var deletes = new List<string>();

        foreach (ManifestFile file in local.Values)
        {
            // preserve 文件永不进入 deletes 集合；新清单里不再列出的 preserve 文件同样不删——它可能是使用者的数据。
            if (file.Policy == FilePolicy.Preserve)
            {
                continue;
            }

            if (!incoming.ContainsKey(file.Path))
            {
                deletes.Add(file.Path);
            }
        }

        return new ManifestDiffResult(writes, deletes, skips);
    }

    private static Dictionary<string, ManifestFile> Index(PackageManifest? manifest)
    {
        var map = new Dictionary<string, ManifestFile>(PathComparer);

        if (manifest is null)
        {
            return map;
        }

        foreach (ManifestFile file in manifest.Files)
        {
            // .smartupdater/ 是更新器自己的状态目录，整个目录不参与文件比对
            if (file.Path.StartsWith(StateDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[file.Path] = file;
        }

        return map;
    }
}
