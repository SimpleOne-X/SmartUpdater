using System.Buffers;

namespace SimpleOneX.SmartUpdater;

/// <summary>Windows 文件名规则的判断：保留设备名、非法字符、结尾的点或空格。校验清单路径与 AppId 时共用，都按单个路径段判断。</summary>
internal static class WindowsFileNames
{
    // 固定的 Windows 字符集，不依赖宿主 OS 的 Path.GetInvalidFileNameChars()（非 Windows 上它几乎只有 '/' 和 '\0'）。
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(
        "\"<>|:*?\\/\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f"
        + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f");

    /// <summary>
    /// 是否是 Windows 保留设备名：CON、PRN、AUX、NUL，以及 COM1–COM9、LPT1–LPT9。不分大小写。
    /// 只比较第一个 '.' 之前的部分：<c>NUL.tar.gz</c> 等同于 <c>NUL</c>，<c>Con.txt</c> 也是设备。
    /// 第一个 '.' 之前的尾随空格也忽略：<c>CON .txt</c> 同样是设备。
    /// COM 与 LPT 必须恰好 4 个字符且第 4 个是 '1'–'9' 或上标 '¹' '²' '³'（Windows 也把 COM¹ 当设备），所以 <c>COM10</c>、<c>COM</c>、<c>LPT0</c> 都不是保留名。
    /// </summary>
    public static bool IsReservedDeviceName(ReadOnlySpan<char> segment)
    {
        int dot = segment.IndexOf('.');
        ReadOnlySpan<char> stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }

    /// <summary>是否含 Windows 文件名的非法字符（固定集合，与宿主 OS 无关）：" &lt; &gt; | : * ? \ / 与 0x00–0x1F。</summary>
    public static bool HasInvalidCharacters(ReadOnlySpan<char> segment)
        => segment.IndexOfAny(InvalidFileNameChars) >= 0;

    /// <summary>是否以 '.' 或空格结尾。Windows 会悄悄删掉它们，磁盘上的名字就和清单里写的对不上了。</summary>
    public static bool EndsWithDotOrSpace(ReadOnlySpan<char> segment)
        => !segment.IsEmpty && segment[^1] is '.' or ' ';
}
