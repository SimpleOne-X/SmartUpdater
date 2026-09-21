namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 直接转发到 System.IO 的生产实现。所有磁盘写操作必须经由 <see cref="IFileOperations"/>，测试才能注入崩溃。
/// <para>
/// <see cref="Move"/>、<see cref="Delete"/>、<see cref="OpenWrite"/> 内置"瞬时失败重试"：Defender 等实时扫描会在文件刚写完、刚改名后
/// 短暂持有它，这几个操作会随机失败几十毫秒（改名覆盖刚写完的文件，改名报 ERROR_ACCESS_DENIED；删除、改名不覆盖、
/// 重新打开报 ERROR_SHARING_VIOLATION）。
/// 只重试 <see cref="UnauthorizedAccessException"/> 与共享冲突 / 锁冲突的 <see cref="IOException"/>；
/// 文件或目录不存在、磁盘满等其他失败立刻抛出。预算用尽时，最后一次的异常原样抛出（同一个对象）。
/// </para>
/// </summary>
internal sealed class PhysicalFileOperations : IFileOperations
{
    /// <summary>一次操作最多尝试的次数（含第一次）。</summary>
    internal const int MaxAttempts = 40;

    // Win32 ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION 对应的 HRESULT。
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);

    private readonly Action<TimeSpan> _sleep;

    /// <summary>唯一的生产实例，重试间隔真的睡眠。</summary>
    public static PhysicalFileOperations Instance { get; } = new(Thread.Sleep);

    /// <summary>测试用：注入睡眠函数，重试就不必真的等。</summary>
    internal PhysicalFileOperations(Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(sleep);
        _sleep = sleep;
    }

    /// <summary>
    /// 第 <paramref name="retryNumber"/>（从 1 起）次重试之前的等待：10、20、30、40 毫秒，之后固定 50 毫秒。
    /// 39 次重试的等待总和是 1850 毫秒，落在约 2 秒的预算内；前几次短，是因为绝大多数瞬时失败一两次重试就过了。
    /// </summary>
    internal static TimeSpan DelayBeforeRetry(int retryNumber)
        => TimeSpan.FromMilliseconds(Math.Min(10 * retryNumber, 50));

    /// <summary>瞬时、值得重试的失败：被拒绝访问，或共享 / 锁冲突。文件或目录不存在等不是。</summary>
    private static bool IsTransient(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException => false,
        IOException io => io.HResult is SharingViolationHResult or LockViolationHResult,
        _ => false,
    };

    /// <summary>
    /// 执行 <paramref name="operation"/>，遇到瞬时失败就睡一会再来，最多 <see cref="MaxAttempts"/> 次。
    /// <paramref name="operation"/> 必须可安全重跑：失败时不能留下半个效果。
    /// </summary>
    internal T Retry<T>(Func<T> operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                _sleep(DelayBeforeRetry(attempt));
            }
        }
    }

    /// <inheritdoc cref="Retry{T}(Func{T})"/>
    internal void Retry(Action operation) => Retry(() =>
    {
        operation();
        return true;
    });

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    // 单次改名要么整个成功要么什么都没变，重跑安全。
    public void Move(string sourcePath, string destinationPath, bool overwrite)
        => Retry(() => File.Move(sourcePath, destinationPath, overwrite));

    // 整个"检查存在 → 清只读 → 删除"可以重跑：文件在两次尝试之间已被别人删掉时，重跑落到"不存在"这一支，是空操作。
    public void Delete(string path) => Retry(() =>
    {
        // File.Delete 对不存在的文件不抛，但目录不存在时抛 DirectoryNotFoundException；只读文件抛 UnauthorizedAccessException。
        if (!File.Exists(path))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    });

    // 打开失败时没有句柄泄漏（FileStream 构造失败会自行释放），重跑安全。
    public Stream OpenWrite(string path)
        => Retry(() => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None));

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public void FlushToDisk(Stream stream)
    {
        if (stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        // 不能用 EnumerateFiles(dir, pattern, SearchOption)：那个重载走 EnumerationOptions.Compatible（IgnoreInaccessible = false），
        // 递归时遇到一个无权访问的子目录就整个抛 UnauthorizedAccessException。启动恢复在 foreach 的头部枚举（位于各自的 try 之外），
        // 一个受限子目录会让整次恢复中止。MatchType / AttributesToSkip 保持与 Compatible 一致，只把无权的项跳过。
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchType = MatchType.Win32,
            AttributesToSkip = 0,
        };

        return [.. Directory.EnumerateFiles(directory, searchPattern, options)];
    }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public long GetFileLength(string path) => new FileInfo(path).Length;
}
