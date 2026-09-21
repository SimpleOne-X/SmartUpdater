namespace SmartUpdater.Tests;

// 两个小工具的关键承诺。它们的失效不会让测试变红，只会悄悄污染结果（时区漂移、临时目录堆积），所以在这里单独确认。
public class TestSupportTests
{
    [Fact]
    public void FakeTimeProvider_local_time_is_UTC_plus_8_regardless_of_the_machine_zone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromHours(8), time.GetLocalNow().Offset);
        Assert.Equal(new DateTime(2026, 9, 20, 0, 0, 0), time.GetLocalNow().DateTime);
        Assert.Same(FakeTimeProvider.EastEight, time.LocalTimeZone);

        time.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(new DateTimeOffset(2026, 9, 19, 17, 30, 0, TimeSpan.Zero), time.GetUtcNow());
        Assert.Equal(time.UtcNow, time.GetUtcNow());
    }

    [Fact]
    public void TempDirectory_round_trips_files_and_normalizes_forward_slashes()
    {
        using var dir = new TempDirectory();

        string written = dir.WriteFile("a/b/c.txt", "中文");

        Assert.Equal(dir.Resolve("a/b/c.txt"), written);
        Assert.Equal(Path.Combine(dir.Root, "a", "b", "c.txt"), written);
        Assert.Equal("中文", dir.ReadFile("a/b/c.txt"));
        Assert.True(dir.Exists("a/b/c.txt"));
        Assert.False(dir.Exists("a/b/missing.txt"));

        // 中文的 UTF-8 字节，且没有 BOM（EF BB BF）。
        byte[] utf8NoBom = [0xE4, 0xB8, 0xAD, 0xE6, 0x96, 0x87];
        Assert.Equal(utf8NoBom, File.ReadAllBytes(written));
    }

    [Fact]
    public void TempDirectory_Dispose_removes_the_whole_tree_including_read_only_files()
    {
        var dir = new TempDirectory();
        string readOnly = dir.WriteFile("sub/ro.txt", "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string root = dir.Root;

        dir.Dispose();

        Assert.False(Directory.Exists(root));
    }
}
