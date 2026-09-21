using System.Text;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class LogTailTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Small_file_is_returned_whole()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteFile("u.log", "line 1\nline 2\n");

        Assert.Equal("line 1\nline 2\n", LogTail.Read(path));
    }

    [Fact]
    public void Large_file_returns_at_most_the_limit_starting_at_a_line_boundary_and_ending_with_the_last_line()
    {
        using var dir = new TempDirectory();
        var content = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            content.Append($"{i:0000} ").Append('x', 94).Append('\n');   // 每行 100 字节
        }

        string path = dir.WriteFile("u.log", content.ToString());
        string tail = LogTail.Read(path);

        Assert.True(Utf8.GetByteCount(tail) <= LogTail.DefaultMaxBytes);
        Assert.True(Utf8.GetByteCount(tail) > LogTail.DefaultMaxBytes - 100);   // 最多只丢半行
        Assert.StartsWith("0119 ", tail, StringComparison.Ordinal);            // 8192 字节 = 81.92 行，跳过第 118 行的残段
        Assert.EndsWith("0199 " + new string('x', 94) + "\n", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_byte_characters_are_never_cut_in_half()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteFile("u.log", new string('修', 10_000));   // 30000 字节，没有换行

        string tail = LogTail.Read(path);

        Assert.DoesNotContain('�', tail);
        Assert.True(Utf8.GetByteCount(tail) <= LogTail.DefaultMaxBytes);
        Assert.True(Utf8.GetByteCount(tail) >= LogTail.DefaultMaxBytes - 3);
        Assert.All(tail, c => Assert.Equal('修', c));
    }

    [Fact]
    public void Custom_limit_is_honoured()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteFile("u.log", "aaaa\nbbbb\ncccc\n");

        Assert.Equal("cccc\n", LogTail.Read(path, maxBytes: 7));
    }

    [Fact]
    public void Missing_or_null_path_yields_empty_string()
    {
        using var dir = new TempDirectory();

        Assert.Equal("", LogTail.Read(dir.Resolve("missing.log")));
        Assert.Equal("", LogTail.Read(null));
    }

    [Fact]
    public void File_held_open_by_a_writer_can_still_be_read()
    {
        using var dir = new TempDirectory();
        string path = dir.Resolve("u.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Utf8.GetBytes("held\n"));
        writer.Flush();

        Assert.Equal("held\n", LogTail.Read(path));
    }

    [Fact]
    public void A_last_line_longer_than_the_limit_is_kept_truncated_instead_of_dropped()
    {
        using var dir = new TempDirectory();
        // 日志文件永远以换行结尾，所以窗口里唯一的 \n 就是最后一个字节
        string path = dir.WriteFile("u.log", "short line\n" + new string('y', 20_000) + "END\n");

        string tail = LogTail.Read(path);

        Assert.True(Utf8.GetByteCount(tail) <= LogTail.DefaultMaxBytes);
        Assert.True(Utf8.GetByteCount(tail) >= LogTail.DefaultMaxBytes - 3);
        Assert.EndsWith("yyyEND\n", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("short", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_file_and_non_positive_limit_yield_empty_string()
    {
        using var dir = new TempDirectory();
        string empty = dir.WriteFile("empty.log", "");
        string path = dir.WriteFile("u.log", "content\n");

        Assert.Equal("", LogTail.Read(empty));
        Assert.Equal("", LogTail.Read(path, maxBytes: 0));
    }
}
