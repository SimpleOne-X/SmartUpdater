namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>一次命令执行的结果：退出码与两路输出文本。</summary>
internal sealed record CommandResult(int ExitCode, string Output, string Error);
