using System.Diagnostics.CodeAnalysis;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 客户端做决策用的版本一律是四段规范形式（Build / Revision 未定义时补 0）。
/// <see cref="Version"/> 认为 1.2.4 与 1.2.4.0 不相等且前者更小；当前版本的来源之一是程序集版本（恒为四段），
/// feed 里常写三段，不统一就会在允许降级时每轮重装、让跳过列表失效、让灰度桶随 ToString() 漂移。
/// 规范化只在客户端决策边界做，JSON 转换器不改：签名覆盖的是 feed 里写的原始条目。
/// </summary>
internal static class VersionNormalization
{
    /// <summary>0.0.0.0。</summary>
    public static Version Zero { get; } = new(0, 0, 0, 0);

    /// <summary>补齐到四段；已是四段则返回同一实例。</summary>
    public static Version Canonical(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return IsCanonical(version)
            ? version
            : new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    /// <summary>Build 与 Revision 都已定义。</summary>
    public static bool IsCanonical(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Build >= 0 && version.Revision >= 0;
    }

    /// <summary>Trim 后按 <see cref="Version.TryParse(string?, out Version?)"/> 解析并规范化；不抛异常。</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        if (Version.TryParse(text?.Trim(), out Version? parsed))
        {
            version = Canonical(parsed);
            return true;
        }

        version = null;
        return false;
    }
}
