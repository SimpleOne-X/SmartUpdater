using System.Text;
using System.Text.RegularExpressions;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 给 <c>--preserve</c> 用的最小 glob。语义：
/// <list type="bullet">
/// <item>模式与路径都用正斜杠；匹配大小写不敏感（Windows 文件系统语义）。</item>
/// <item><c>*</c> 匹配同一段内任意个字符，不跨 <c>/</c>。</item>
/// <item><c>?</c> 匹配同一段内恰好一个字符（按 UTF-16 代码单元计），不跨 <c>/</c>。</item>
/// <item><c>**</c> 只能作为完整的一段出现，匹配零或多段；作为最后一段时至少匹配一段
/// （<c>config/**</c> 匹配 <c>config/a.json</c>，不匹配 <c>config</c> 本身）。</item>
/// <item>其余字符字面匹配（<c>.</c> 不是元字符）。</item>
/// <item>不支持 <c>[]</c> 字符类与 <c>{}</c> 花括号展开，出现即拒绝，而不是当字面量——
/// 当字面量会让写了 <c>*.{json,xml}</c> 的人以为自己保护了文件，实则一个都没匹配上。</item>
/// <item>空模式、含 <c>\</c> 的模式、含 <c>**</c> 但不独占一段的模式（如 <c>a**b</c>）都不合法。</item>
/// <item>空段（首尾或连续的 <c>/</c>：<c>/a</c>、<c>config/</c>、<c>a//b</c>）、<c>.</c> 与 <c>..</c> 段、
/// 含 <c>:</c> 的模式（Windows 文件名里非法）都不合法：它们永远匹配不上任何路径。
/// 末尾带 <c>/</c> 的模式，报错会提示目录应写成 <c>dir/**</c>。</item>
/// </list>
/// <para><see cref="Matches"/> 不做分隔符归一：调用方必须传<b>正斜杠</b>相对路径
/// （传含 <c>\</c> 的路径时 <c>*.json</c> 会匹配 <c>config\a.json</c>，静默变成跨目录）。</para>
/// 实现是把模式翻译成 <see cref="Regex"/>（BCL；Packer 不受 AOT 约束）。
/// </summary>
internal static class GlobMatcher
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>检查模式是否合法（不支持的字符类 / 花括号、空模式、反斜杠、<c>**</c> 不独占一段）。调用方在收参数时就该校验，不合法要当场报错。</summary>
    public static bool IsValidPattern(string pattern, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "glob 模式不能为空。";
            return false;
        }

        if (pattern.Contains('\\', StringComparison.Ordinal))
        {
            error = $"glob 模式 {pattern} 含反斜杠 \\：模式一律用正斜杠 / 分隔路径。";
            return false;
        }

        foreach (char c in "[]{}")
        {
            if (pattern.Contains(c, StringComparison.Ordinal))
            {
                error = $"glob 模式 {pattern} 含 {c}：不支持 [] 字符类与 {{}} 花括号展开，也不会把它们当字面量处理。请拆成多个模式。";
                return false;
            }
        }

        // 路径相对安装目录、不含 : 与空段与 . / .. 段——这些写法永远匹配不上任何文件，
        // 放行只会让写错模式的人以为自己保护了文件（与拒绝 [] 和 {} 的理由相同）。
        if (pattern.Contains(':', StringComparison.Ordinal))
        {
            error = $"glob 模式 {pattern} 含 :：Windows 文件名里不允许出现 :，模式不能写盘符（如 C:/x）或数据流。";
            return false;
        }

        string[] segments = pattern.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (segment.Length == 0)
            {
                error = i == 0
                    ? $"glob 模式 {pattern} 以 / 开头：路径相对安装目录，不写开头的 /。"
                    : i == segments.Length - 1
                        ? $"glob 模式 {pattern} 以 / 结尾：目录请写成 {pattern}**（匹配该目录下的全部文件）。"
                        : $"glob 模式 {pattern} 含连续的 /（空路径段）。";
                return false;
            }

            if (segment is "." or "..")
            {
                error = $"glob 模式 {pattern} 含 {segment} 段：路径里不会出现 . 与 ..，这样的模式永远匹配不上。";
                return false;
            }

            if (segment.Contains("**", StringComparison.Ordinal) && segment != "**")
            {
                error = $"glob 模式 {pattern} 里的 ** 必须独占一段（如 a/**/b），不能写成 {segment}。";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 路径（正斜杠、相对路径）是否整串匹配模式。
    /// 本方法<b>不</b>校验模式：调用方应先用 <see cref="IsValidPattern"/> 校验；
    /// 对不合法的模式，本方法的返回值不做任何承诺（但不会抛异常）。
    /// </summary>
    public static bool Matches(string pattern, string path)
        // 用 \A…\z 而不是 ^…$：$ 会在结尾换行符之前匹配，"a.json\n" 会被误认成 "a.json"。
        => Regex.IsMatch(path, $@"\A{Translate(pattern)}\z", Options);

    /// <summary>
    /// 把模式翻译成（未锚定的）正则字符串。单独暴露是为了调试时能直接看到翻译结果：
    /// <c>x/**/y</c> → <c>x/(?:[^/]+/)*y</c>；<c>config/**</c> → <c>config/(?:[^/]+/)*[^/]+</c>；
    /// <c>**/*.db</c> → <c>(?:[^/]+/)*[^/]*\.db</c>。
    /// </summary>
    internal static string Translate(string pattern)
    {
        string[] segments = pattern.Split('/');
        StringBuilder regex = new();

        for (int i = 0; i < segments.Length; i++)
        {
            bool isLast = i == segments.Length - 1;

            if (segments[i] == "**")
            {
                // 非末段：连同它后面的 / 一起吞掉零或多段，所以 a/**/b 能匹配 a/b。
                // 末段：至少要有一段，所以 config/** 不匹配 config 本身。
                regex.Append(isLast ? "(?:[^/]+/)*[^/]+" : "(?:[^/]+/)*");
                continue;
            }

            foreach (char c in segments[i])
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }

            if (!isLast)
            {
                regex.Append('/');
            }
        }

        return regex.ToString();
    }
}
