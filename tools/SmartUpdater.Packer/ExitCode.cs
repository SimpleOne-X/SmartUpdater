namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>进程退出码。端到端脚本靠它区分"参数写错了"与"真的冲突了"。</summary>
internal static class ExitCode
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int Input = 2;
    public const int Conflict = 3;
    public const int Unexpected = 4;
}
