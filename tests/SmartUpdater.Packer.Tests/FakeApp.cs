using System.Security.Cryptography;
using System.Text;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

/// <summary>在临时目录里造一个假的"已发布应用"目录。</summary>
internal sealed class FakeApp : IDisposable
{
    private readonly TempDirectory _temp = new();

    public string Root => _temp.Path;

    /// <summary>写一个文件；relativePath 用正斜杠。</summary>
    public FakeApp With(string relativePath, string content)
    {
        string full = Path.Combine(Root, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return this;
    }

    public FakeApp WithBytes(string relativePath, byte[] content)
    {
        string full = Path.Combine(Root, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return this;
    }

    /// <summary>一个典型的小应用：主 exe、一个 dll、一个配置文件、一个子目录资源。</summary>
    public static FakeApp Standard()
        => new FakeApp()
            .With("MyApp.exe", "exe v1")
            .With("MyApp.dll", "dll v1")
            .With("appsettings.json", """{"setting":1}""")
            .With("Resources/logo.png", "png v1");

    /// <summary>把每个源文件的最后写入时间推后，用来证明 zip 的决定论不受它影响。</summary>
    public void TouchAll()
    {
        foreach (string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(7));
        }
    }

    public static string Sha256Hex(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public void Dispose() => _temp.Dispose();
}
