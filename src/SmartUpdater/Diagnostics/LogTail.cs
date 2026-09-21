using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>读日志文件的尾部，供 <c>Failed</c> 上报附带。失败原因通常在最后，所以超限时保留尾部。</summary>
internal static class LogTail
{
    /// <summary>默认上限：8 KB。</summary>
    public const int DefaultMaxBytes = 8 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 读文件最后至多 <paramref name="maxBytes"/> 个字节，尽量从行首开始，且不切断 UTF-8 字符。
    /// 文件不存在、不可读或路径为 null 时返回空字符串。
    /// </summary>
    public static string Read(string? filePath, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(filePath) || maxBytes <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            int count = (int)Math.Min(length, maxBytes);
            if (count == 0)
            {
                return string.Empty;
            }

            stream.Seek(length - count, SeekOrigin.Begin);
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            int offset = length > maxBytes ? FindStart(buffer.AsSpan(0, read)) : 0;
            return Utf8.GetString(buffer, offset, read - offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 契约：不可读 = 没有尾部可附带；调用方（上报）不需要区分原因，也不能因此失败。
            return string.Empty;
        }
    }

    /// <summary>截断后的第一个可用字节：优先取第一个换行之后（行首），否则跳过开头的 UTF-8 续字节。</summary>
    private static int FindStart(ReadOnlySpan<byte> window)
    {
        int newline = window.IndexOf((byte)'\n');

        // 换行恰好是最后一个字节，说明窗口里只有一条被截断的超长行：整段丢掉会让尾部变成空，
        // 而"失败原因在最后"恰恰就在这一行里，所以退回到只保证不切断字符。
        if (newline >= 0 && newline + 1 < window.Length)
        {
            return newline + 1;
        }

        int offset = 0;
        while (offset < window.Length && (window[offset] & 0xC0) == 0x80)
        {
            offset++;
        }

        return offset;
    }
}
