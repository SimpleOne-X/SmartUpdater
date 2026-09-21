using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater;

/// <summary>升级包校验：包级 SHA-256（强制）与可选的 ECDSA 验签。</summary>
internal static class PackageVerifier
{
    private const int BufferSize = 81920;

    /// <summary>
    /// 顺序：文件存在、长度、SHA-256（不分大小写）、配置了公钥时再验签。任一失败抛 <see cref="UpdateFailedException"/>（<see cref="UpdateStage.Verify"/>）。
    /// <paramref name="release"/> 必须是 <see cref="ValidatedFeed.RawOf"/> 返回的原始条目（签名覆盖原始写法）。
    /// </summary>
    public static void Verify(ReleaseEntry release, string packagePath, string? publicKeySpkiBase64, IUpdateLog log, IProgress<long>? hashedBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        var info = new FileInfo(packagePath);
        if (!info.Exists)
        {
            throw new UpdateFailedException(UpdateStage.Verify, $"升级包不存在：{packagePath}", new FileNotFoundException(packagePath));
        }

        long length;
        try
        {
            length = info.Length;
        }
        catch (IOException ex)
        {
            throw new UpdateFailedException(UpdateStage.Verify, $"无法读取升级包：{ex.Message}", ex);
        }

        if (length != release.Package.Size)
        {
            throw new UpdateFailedException(UpdateStage.Verify, $"升级包大小不符：期望 {release.Package.Size} 字节，实际 {length} 字节");
        }

        string actual;
        try
        {
            actual = ComputeSha256(packagePath, hashedBytes, ct);
        }
        catch (IOException ex)
        {
            throw new UpdateFailedException(UpdateStage.Verify, $"无法读取升级包：{ex.Message}", ex);
        }

        string expected = release.Package.Sha256;
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            string message = $"包哈希不匹配：期望 {expected}，实际 {actual}";
            log.Error(UpdateStage.Verify, message);
            throw new UpdateFailedException(UpdateStage.Verify, message);
        }

        log.Information(UpdateStage.Verify, $"包哈希匹配：{actual}");

        if (publicKeySpkiBase64 is not null)
        {
            if (string.IsNullOrWhiteSpace(release.Signature))
            {
                throw new UpdateFailedException(UpdateStage.Verify, $"feed 条目 {release.Version} 缺少签名，而客户端已配置公钥");
            }

            ECDsa key;
            try
            {
                key = ReleaseSignature.ImportPublicKey(publicKeySpkiBase64);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new UpdateFailedException(UpdateStage.Verify, "配置的公钥无效", ex);
            }

            using (key)
            {
                if (!ReleaseSignature.Verify(release, key))
                {
                    throw new UpdateFailedException(UpdateStage.Verify, $"feed 条目 {release.Version} 的签名不匹配");
                }
            }

            log.Information(UpdateStage.Verify, "签名验证通过");
        }
    }

    /// <summary>分块流式 SHA-256（小写十六进制），每次读取后上报累计字节数；<paramref name="ct"/> 可取消。</summary>
    public static string ComputeSha256(string path, IProgress<long>? progress, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            total += read;
            progress?.Report(total);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
