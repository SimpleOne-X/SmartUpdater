namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 手写的选项解析器，不依赖第三方命令行库。规则很小，全部由 ArgumentParserTests 表驱动地钉死：
/// <list type="bullet">
/// <item>第一个 token 是命令名，必须是 pack / sign / help 之一。</item>
/// <item>选项以 <c>--</c> 开头，名字须匹配 <c>^[a-z][a-z0-9-]*$</c>（kebab-case）。</item>
/// <item><c>--name=value</c> 在第一个 <c>=</c> 处切分，右侧即使为空串也算给了值。</item>
/// <item><c>--name value</c>：下一个 token 不以 <c>--</c> 开头时把它当值。</item>
/// <item>下一个 token 以 <c>--</c> 开头（或已到末尾）时，本选项没有值，是个开关。</item>
/// <item>同名选项可重复出现，按出现顺序保留。</item>
/// <item>命令名之后出现的、没被上面"--name value"规则消费的裸 token 是错误。</item>
/// </list>
/// 开关规则的代价：值不能以 <c>--</c> 开头，需要时写成 <c>--notes=--例外</c>（<c>--name=value</c> 形式）。
/// 所有报错里的选项名都带 <c>--</c> 前缀，方便用户直接对照自己敲的命令。
/// </summary>
internal static class ArgumentParser
{
    private const string OptionPrefix = "--";

    private static readonly string[] Commands = ["pack", "sign", "help"];

    public static bool TryParse(string[] args, out ParsedCommandLine? parsed, out string? error)
    {
        parsed = null;

        if (args.Length == 0)
        {
            error = $"缺少命令。可用命令：{string.Join("、", Commands)}。";
            return false;
        }

        string command = args[0];
        if (!Commands.Contains(command, StringComparer.Ordinal))
        {
            error = $"未知命令 {command}。可用命令：{string.Join("、", Commands)}。";
            return false;
        }

        List<ParsedOption> options = [];
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith(OptionPrefix, StringComparison.Ordinal))
            {
                error = $"多余的参数 \"{token}\"：选项必须以 {OptionPrefix} 开头，选项的值紧跟在选项名之后。";
                return false;
            }

            string body = token[OptionPrefix.Length..];
            int equalsIndex = body.IndexOf('=', StringComparison.Ordinal);
            string name = equalsIndex < 0 ? body : body[..equalsIndex];
            if (!IsValidOptionName(name))
            {
                error = $"选项名 {OptionPrefix}{name} 不合法：选项名只能由小写字母、数字和连字符组成，且以小写字母开头（如 --min-updatable-from）。";
                return false;
            }

            if (equalsIndex >= 0)
            {
                options.Add(new ParsedOption(name, body[(equalsIndex + 1)..]));
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith(OptionPrefix, StringComparison.Ordinal))
            {
                options.Add(new ParsedOption(name, args[++i]));
            }
            else
            {
                options.Add(new ParsedOption(name, null));
            }
        }

        parsed = new ParsedCommandLine(command, options);
        error = null;
        return true;
    }

    /// <summary>手写的 <c>^[a-z][a-z0-9-]*$</c>；空串（token 就是 <c>--</c>）自然落到不合法。</summary>
    private static bool IsValidOptionName(string name)
    {
        if (name.Length == 0 || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
