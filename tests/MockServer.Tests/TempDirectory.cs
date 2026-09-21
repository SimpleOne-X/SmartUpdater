namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>一次性的临时目录，Dispose 时递归删除。测试不碰任何真实用户目录。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sumock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void WriteText(string relativePath, string content)
    {
        string full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void WriteBytes(string relativePath, byte[] content)
    {
        string full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 测试目录清理失败不应让测试变红。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
