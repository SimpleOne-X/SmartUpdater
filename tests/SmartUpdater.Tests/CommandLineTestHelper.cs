using System.Text;

namespace SmartUpdater.Tests;

/// <summary>
/// 按 CommandLineToArgvW 的规则把命令行拆回参数数组，用于往返验证。
/// 适用范围：仅保证对 <c>CommandLine.Build</c> 的产物与 Windows 解析一致；不模拟 CRT 的“引号内连续两个引号 = 一个字面引号”规则，
/// 也不处理 argv[0] 的特殊规则（<c>Quote</c> 除空参数的 <c>""</c> 外不会产出相邻的未转义引号，所以这两点不影响往返验证；
/// 而对含相邻引号的手写命令行，shell32 与 CRT 的解析结果本身就有分歧，没有单一的“Windows 语义”可模拟，因此不要用本类解析手写命令行）。
/// </summary>
internal static class CommandLineTestHelper
{
    public static string[] SplitLikeWindows(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        int backslashes = 0;
        bool hasToken = false;

        void FlushBackslashes(bool beforeQuote)
        {
            if (beforeQuote)
            {
                current.Append('\\', backslashes / 2);

                if (backslashes % 2 == 1)
                {
                    current.Append('"');
                }
            }
            else
            {
                current.Append('\\', backslashes);
            }

            backslashes = 0;
        }

        foreach (char c in commandLine)
        {
            if (c == '\\')
            {
                backslashes++;
                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                bool literalQuote = backslashes % 2 == 1;
                FlushBackslashes(beforeQuote: true);

                if (!literalQuote)
                {
                    inQuotes = !inQuotes;
                }

                hasToken = true;
                continue;
            }

            FlushBackslashes(beforeQuote: false);

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        FlushBackslashes(beforeQuote: false);

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return [.. result];
    }
}
