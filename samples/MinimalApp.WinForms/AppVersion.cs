using System.Reflection;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>
/// 当前运行版本的唯一来源。取 AssemblyInformationalVersion 而不是 GetName().Version：
/// 后者恒为四段（1.1.0 会变成 1.1.0.0），而 feed 里写的是发布者给 pack --version 的原字符串；
/// System.Version 下 1.1.0 != 1.1.0.0，灰度分桶也会因此漂移。
/// </summary>
internal static class AppVersion
{
    public static Version Current { get; } = Resolve();

    /// <summary>解析 informational 版本串；含 '+' 时截到 '+' 之前（SourceLink / CI 会追加提交哈希）。</summary>
    public static bool TryParseInformational(string? informational, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(informational))
        {
            return false;
        }

        string text = informational;
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        // "+1.0.0" 截断后是空串，Version.TryParse 会拒绝它；"1" 只有一段，同样被拒绝（只接受 2~4 段）。
        if (!Version.TryParse(text, out Version? parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static Version Resolve()
    {
        Assembly assembly = typeof(AppVersion).Assembly;

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseInformational(informational, out Version version))
        {
            return version;
        }

        return assembly.GetName().Version ?? new Version(0, 0);
    }
}
