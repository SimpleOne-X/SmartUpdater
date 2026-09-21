namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 从本地目录或 UNC 共享复制升级包。与 <see cref="HttpPackageDownloader"/> 共用同一套 <c>.part</c> 续传、进度与取消语义，但没有退避。
/// </summary>
internal sealed class FilePackageDownloader : IPackageDownloader
{
    private const int BufferSize = 64 * 1024;

    private readonly TimeProvider _timeProvider;

    public FilePackageDownloader(string baseDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        BaseDirectory = Path.GetFullPath(baseDirectory);
        _timeProvider = timeProvider;
    }

    public string BaseDirectory { get; }

    public async Task<string> DownloadAsync(ReleaseEntry release, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string sourcePath = ResolvePackagePath(BaseDirectory, release.Package.Url);
        long size = release.Package.Size;
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == size)
        {
            return destinationPath;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"找不到升级包文件 {sourcePath}", sourcePath);
        }

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string part = destinationPath + HttpPackageDownloader.PartSuffix;
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset >= size)
        {
            File.Delete(part);
            offset = 0;
        }

        var transfer = new TransferProgress(size, offset, _timeProvider, progress);
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true))
        {
            source.Seek(offset, SeekOrigin.Begin);
            using FileStream target = await HttpPackageDownloader.OpenPartForWriteAsync(part, offset > 0, ct).ConfigureAwait(false);
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int n = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                transfer.Add(n);
            }
        }

        long length = new FileInfo(part).Length;
        if (length != size)
        {
            throw new IOException($"包文件长度 {length} 与 feed 声明 {size} 不符");
        }

        File.Move(part, destinationPath, overwrite: true);
        transfer.Complete();
        return destinationPath;
    }

    internal static string ResolvePackagePath(string baseDirectory, string packageUrl)
    {
        if (Uri.TryCreate(packageUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (Path.IsPathRooted(packageUrl))
        {
            return packageUrl;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, packageUrl.Replace('/', Path.DirectorySeparatorChar)));
    }
}
