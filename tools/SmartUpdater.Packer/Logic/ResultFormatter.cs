using System.Text;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>把 <see cref="CommandResult"/> 排成日志区里的一段文本：标题行（命令、成败、退出码含义）加两路输出。</summary>
internal static class ResultFormatter
{
    /// <summary>格式化一次执行结果；返回值末尾不带换行。</summary>
    public static string Format(string command, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();
        text.Append('[').Append(command).Append("] ")
            .Append(result.ExitCode == ExitCode.Success ? "成功" : "失败")
            .Append("（退出码 ").Append(result.ExitCode).Append("：").Append(Describe(result.ExitCode)).Append('）');

        AppendSection(text, result.Output);
        AppendSection(text, result.Error);
        return text.ToString();
    }

    private static void AppendSection(StringBuilder text, string section)
    {
        string trimmed = section.TrimEnd('\r', '\n', ' ');
        if (trimmed.Length > 0)
        {
            text.Append('\n').Append(trimmed);
        }
    }

    private static string Describe(int exitCode) => exitCode switch
    {
        ExitCode.Success => "成功",
        ExitCode.Usage => "用法错误",
        ExitCode.Input => "输入有问题",
        ExitCode.Conflict => "冲突（同版本内容不同时需要强制替换）",
        ExitCode.Unexpected => "意外错误",
        _ => "未知退出码",
    };
}
