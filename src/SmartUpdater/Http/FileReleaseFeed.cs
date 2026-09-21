using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 从本地路径或 UNC 路径读取同格式的 feed，以文件内容的 SHA-256 作 ETag。
/// 安全提示：文件路径没有 TLS 保护，本类不做额外校验，完整性由发布签名保证。
/// IO 异常（<see cref="FileNotFoundException"/>、<see cref="DirectoryNotFoundException"/> 等）原样传播。
/// </summary>
public sealed class FileReleaseFeed : IReleaseFeed
{
    /// <summary>ETag 的前缀，后接小写十六进制的 SHA-256。</summary>
    public const string ETagPrefix = "sha256:";

    /// <summary>创建实例；相对路径按当前目录转成绝对路径，不检查文件是否存在。</summary>
    /// <param name="feedPath">feed 文件的路径。</param>
    /// <exception cref="ArgumentException"><paramref name="feedPath"/> 为 null、空或空白。</exception>
    public FileReleaseFeed(string feedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedPath);
        FeedPath = Path.GetFullPath(feedPath);
        FeedDirectory = Path.GetDirectoryName(FeedPath) ?? FeedPath;
        FeedUri = new Uri(FeedPath);
    }

    /// <summary>feed 文件的绝对路径。</summary>
    public string FeedPath { get; }

    /// <summary>feed 文件所在目录。</summary>
    public string FeedDirectory { get; }

    /// <summary>feed 文件的 file:// 地址。</summary>
    public Uri FeedUri { get; }

    /// <inheritdoc />
    public async Task<FeedResult> GetAsync(string? etag, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(FeedPath, ct).ConfigureAwait(false);
        string currentETag = ETagPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (string.Equals(etag, currentETag, StringComparison.Ordinal))
        {
            return FeedResult.NotModified;
        }

        ReleaseFeedDocument document = ReleaseFeedReader.Parse(bytes);
        return new FeedResult(false, document, currentETag);
    }
}
