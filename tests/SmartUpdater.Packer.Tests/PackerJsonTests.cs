using System.Text;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class PackerJsonTests
{
    [Fact]
    public void WriteAtomic_creates_the_file_and_leaves_no_temp_file()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("releases.json");

        PackerJson.WriteAtomic(path, "{\"a\":1}"u8);

        Assert.Equal("{\"a\":1}", File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void WriteAtomic_replaces_an_existing_file()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("releases.json");
        File.WriteAllText(path, "old content that is longer than the new one");

        PackerJson.WriteAtomic(path, "new"u8);

        Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void WriteAtomic_overwrites_a_stale_temp_file_left_by_a_crash()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("releases.json");
        File.WriteAllText(path + ".tmp", "garbage from a crashed run, longer than the payload");

        PackerJson.WriteAtomic(path, "ok"u8);

        Assert.Equal("ok", File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void WriteAtomic_removes_its_temp_file_when_the_replace_fails()
    {
        using var temp = new TempDirectory();
        // 目标路径是个目录：写临时文件成功，随后的 File.Move 必然失败。
        string path = temp.Combine("releases.json");
        Directory.CreateDirectory(path);

        // 具体异常类型随平台而异（Windows 上是 UnauthorizedAccessException，Linux 上是 IOException）；
        // 这里只关心：失败被如实抛出、临时文件被清掉、原目标没被破坏。
        Exception? error = Record.Exception(() => PackerJson.WriteAtomic(path, "x"u8));

        Assert.NotNull(error);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(Directory.Exists(path));
    }
}
