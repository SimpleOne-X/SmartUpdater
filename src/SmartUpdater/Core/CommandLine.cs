using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>跨进程交接用的命令行参数名。集中定义，避免两端各写一遍字面量而静默失效。</summary>
internal static class SmartUpdaterArgs
{
    /// <summary>新版本首次启动时携带，值为刚安装的版本号。</summary>
    public const string Updated = "--smartupdater-updated";

    /// <summary>新版本启动时携带，值为旧进程 PID，新进程需等其退出。</summary>
    public const string WaitPid = "--smartupdater-wait-pid";

    /// <summary>人工恢复入口：把 *.suold 改回原名。</summary>
    public const string Rollback = "--smartupdater-rollback";
}

/// <summary>Windows 命令行的构造与解析。</summary>
internal static class CommandLine
{
    /// <summary>按 Windows 规则给单个参数加引号与转义。</summary>
    /// <remarks>
    /// 依据 <c>CommandLineToArgvW</c> 的公开语义：反斜杠只有在紧挨引号时才需要翻倍，
    /// 单独出现时保持原样。写错会让含空格的路径在交接时被拆成多个参数。
    /// </remarks>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !argument.AsSpan().ContainsAny(' ', '\t', '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        int backslashes = 0;

        foreach (char c in argument)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;

                case '"':
                    // 引号前的反斜杠翻倍，再转义引号本身
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    break;

                default:
                    builder.Append('\\', backslashes);
                    builder.Append(c);
                    backslashes = 0;
                    break;
            }
        }

        // 尾部反斜杠紧挨闭合引号，必须翻倍，否则闭合引号会被转义掉
        builder.Append('\\', backslashes * 2);
        builder.Append('"');

        return builder.ToString();
    }

    /// <summary>把多个参数拼成一条命令行。</summary>
    public static string Build(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return string.Join(' ', arguments.Select(Quote));
    }

    /// <summary>读取某个选项后面紧跟的值。选项缺失、无值、或下一个 token 也是选项时返回 null。</summary>
    /// <remarks>
    /// 选项名不区分大小写，且须与整个 token 相等（只是前缀相同的另一个选项不算）。
    /// 同名选项重复出现时只看第一个：即使第一个没有值、后面的重复项带值，也返回 null，不会继续往后找。
    /// 选项后面没有参数（选项在末尾），或紧跟的参数以 <c>--</c> 开头，都视为"没有值"而返回 null；
    /// 因此以 <c>--</c> 开头的值本身读不出来。选项缺失与选项没有值同样返回 null，二者无法从返回值区分。
    /// 空字符串是合法的值，原样返回。
    /// </remarks>
    public static string? GetOptionValue(string[] args, string optionName)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(optionName);

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = args[i + 1];
            return candidate.StartsWith("--", StringComparison.Ordinal) ? null : candidate;
        }

        return null;
    }

    /// <summary>判断某个无值开关是否出现。</summary>
    public static bool HasFlag(string[] args, string optionName)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(optionName);

        return args.Any(a => string.Equals(a, optionName, StringComparison.OrdinalIgnoreCase));
    }
}
