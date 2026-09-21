using System.Security.Cryptography;
using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>推导应用标识。它是多应用隔离的唯一支点：缓存目录、日志目录、单实例 Mutex 名都以它为准。</summary>
internal static class AppIdentity
{
    private const int HashHexLength = 8;
    private const string Fallback = "app";

    /// <summary>按程序集名与主程序完整路径推导 AppId。</summary>
    /// <param name="assemblyName">入口程序集名。</param>
    /// <param name="executablePath">主程序的完整路径。</param>
    /// <remarks>
    /// 必须带路径哈希：同一个程序完全可能装两份（正式目录与测试目录各一个），
    /// 仅靠程序集名两边会共用同一个缓存和日志目录，互相覆盖。
    /// </remarks>
    public static string Derive(string assemblyName, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string safeName = Sanitize(assemblyName);

        string normalizedPath = executablePath
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

        // 哈希输入用原始程序集名，不是清洗后的：
        // "My App" 与 "My/App" 清洗后都是 "My_App"，只有原始名能把它们区分开。
        string material = $"{assemblyName}|{normalizedPath}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);

        string suffix = Convert.ToHexStringLower(hash[..(HashHexLength / 2)]);
        return $"{safeName}-{suffix}";
    }

    /// <summary>只校验字符集：非空，且只含 ASCII 字母、数字与 '-' '_' '.'。</summary>
    /// <remarks>
    /// 不保证：不是 "." / ".." 等全点号名，不是保留设备名（如 CON），不以点结尾，长度合适。
    /// 校验外部传入（UpdateClientOptions.AppId）的 AppId 时需另行处理。
    /// </remarks>
    public static bool IsValid(string appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return false;
        }

        foreach (char c in appId)
        {
            if (!IsSafe(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        var builder = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            builder.Append(IsSafe(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static bool IsSafe(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
