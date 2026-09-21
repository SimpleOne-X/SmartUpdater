namespace SimpleOneX.SmartUpdater;

/// <summary>单个卷上的空间检查：需要多少、当前有多少（未知为 <see langword="null"/>）。</summary>
internal readonly record struct VolumeCheck(string Root, long RequiredBytes, long? AvailableBytes)
{
    /// <summary>空闲空间无法确定时视为充足：诊断失败不阻止更新。</summary>
    public bool IsSufficient => AvailableBytes is null || AvailableBytes.Value >= RequiredBytes;
}

/// <summary>磁盘预检的结果：安装卷一定有；下载缓存与安装目录不在同一卷时才有单独的缓存卷检查。</summary>
internal readonly record struct DiskSpaceCheckResult(VolumeCheck Install, VolumeCheck? Cache)
{
    public bool IsSufficient => Install.IsSufficient && (Cache?.IsSufficient ?? true);

    public long RequiredBytes => Install.RequiredBytes + (Cache?.RequiredBytes ?? 0);
}

/// <summary>阈值 = zip 体积 + manifest 全部文件 size 之和 × 1.2；按卷检查。</summary>
internal static class DiskSpaceChecker
{
    public const double SafetyFactor = 1.2;

    /// <summary>安装卷上 <c>.sunew</c> 需要的字节数：<c>ceil(totalFileBytes × 1.2)</c>。</summary>
    public static long ComputeInstallBytes(long totalFileBytes)
        => (long)Math.Ceiling(totalFileBytes * SafetyFactor);

    public static string VolumeRootOf(string path)
        => Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

    /// <summary>所在卷的空闲字节数；无法确定（UNC、卷不可用、无权限）返回 <see langword="null"/>。</summary>
    public static long? GetAvailableBytes(string path)
    {
        try
        {
            return new DriveInfo(VolumeRootOf(path)).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return null;   // UNC 路径等 DriveInfo 不认识的根
        }
        catch (IOException)
        {
            return null;   // 卷不可用（如已拔出的可移动盘）
        }
        catch (UnauthorizedAccessException)
        {
            return null;   // 无权读取该卷信息（官方文档列出的异常之一）
        }
    }

    /// <summary>
    /// 同卷：一个检查，需要 zip + <c>.sunew</c>；不同卷：安装卷只需 <c>.sunew</c>，缓存卷只需 zip。
    /// <paramref name="availableBytesOf"/> 以卷根为参数，可注入。
    /// </summary>
    public static DiskSpaceCheckResult Check(
        long packageBytes,
        long totalFileBytes,
        string installDirectory,
        string downloadCacheDirectory,
        Func<string, long?> availableBytesOf)
    {
        ArgumentNullException.ThrowIfNull(availableBytesOf);

        string installRoot = VolumeRootOf(installDirectory);
        string cacheRoot = VolumeRootOf(downloadCacheDirectory);
        long installBytes = ComputeInstallBytes(totalFileBytes);

        if (string.Equals(installRoot, cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DiskSpaceCheckResult(
                new VolumeCheck(installRoot, packageBytes + installBytes, availableBytesOf(installRoot)),
                Cache: null);
        }

        return new DiskSpaceCheckResult(
            new VolumeCheck(installRoot, installBytes, availableBytesOf(installRoot)),
            new VolumeCheck(cacheRoot, packageBytes, availableBytesOf(cacheRoot)));
    }
}
