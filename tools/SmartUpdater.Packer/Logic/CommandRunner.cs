namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 在进程内执行 pack / sign：走与命令行完全相同的解析器与命令入口（<see cref="PackCommand"/>、<see cref="SignCommand"/>），
/// 所以退出码与消息和命令行一致；不复制任何打包或签名逻辑。
/// </summary>
internal static class CommandRunner
{
    /// <summary>同步执行一条命令并收集输出；调用方负责放到后台线程。</summary>
    public static CommandResult Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var output = new StringWriter();
        var error = new StringWriter();

        if (!ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? parseError))
        {
            error.WriteLine(parseError);
            return new CommandResult(ExitCode.Usage, output.ToString(), error.ToString());
        }

        int code = parsed!.Command switch
        {
            "pack" => PackCommand.Run(parsed, output, error),
            "sign" => SignCommand.Run(parsed, output, error),
            _ => Unsupported(parsed.Command, error),
        };

        return new CommandResult(code, output.ToString(), error.ToString());
    }

    private static int Unsupported(string command, TextWriter error)
    {
        error.WriteLine($"界面不支持命令 {command}。");
        return ExitCode.Usage;
    }
}
