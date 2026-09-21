using System.Text;

namespace SmartUpdater.Tests;

/// <summary>每个测试独占的临时目录，位于 %TEMP%\SmartUpdaterTests\&lt;Guid&gt;，测完递归删除。绝不触碰真实 %LOCALAPPDATA%。</summary>
internal sealed class TempDirectory : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "SmartUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Resolve(string relativePath)
        => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string WriteFile(string relativePath, string content)
    {
        string full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Utf8NoBom);
        return full;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        string full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Resolve(relativePath), Utf8NoBom);

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 尽力而为：被外部句柄占住的文件留给系统临时目录清理，不让清理失败掩盖真正的测试结果。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
