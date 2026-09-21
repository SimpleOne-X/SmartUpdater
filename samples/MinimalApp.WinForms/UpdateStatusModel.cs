namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>界面上那个小标签的语义色。故意不用 AntdUI 的枚举：本文件必须零 UI 依赖才能被单测。</summary>
internal enum StatusKind
{
    Neutral,
    Attention,
    Working,
    Good,
    Bad,
}

/// <summary>进度所处的阶段。与包的 UpdateStage 是两回事：这里只有界面需要区分的三个。</summary>
internal enum UpdatePhase
{
    Download,
    Verify,
    Commit,
}

/// <summary>
/// 把更新过程翻译成界面文字与控件状态。<b>零 UI、零包依赖</b>，因此可以被 xunit 直接测。
/// 界面（MainWindow）只负责把这个对象的字段摆到控件上。
/// </summary>
internal sealed record UpdateStatusModel(
    string StatusText,
    float ProgressValue,
    string TagText,
    StatusKind TagKind,
    bool CheckButtonEnabled)
{
    public static UpdateStatusModel Idle(Version current) =>
        new($"当前版本 {current}，尚未检查更新。", 0F, "已是最新", StatusKind.Neutral, true);

    public static UpdateStatusModel Checking(Version current) =>
        new("正在检查更新……", 0F, "检查中", StatusKind.Working, false);

    public static UpdateStatusModel Available(Version current, Version candidate) =>
        new($"发现新版本 {candidate}（当前 {current}）。", 0F, "有更新", StatusKind.Attention, true);

    public static UpdateStatusModel Mandatory(Version candidate) =>
        new($"强制更新中（{candidate}）……", 0F, "强制更新", StatusKind.Working, false);

    public static UpdateStatusModel Progress(UpdatePhase phase, Version candidate, double percent)
    {
        // AntdUI 的 Progress.Value 取值 0..1；NaN（比如 0/0）按 0 处理，
        // 不然它会原样流进进度条，也没法强转成整数百分比。
        double clamped = double.IsNaN(percent) ? 0.0 : Math.Clamp(percent, 0.0, 1.0);

        // 必须显式 AwayFromZero：默认的 banker's rounding 会让 42.5 变成 42，而界面文案要 43。
        int wholePercent = (int)Math.Round(clamped * 100, MidpointRounding.AwayFromZero);

        string verb = phase switch
        {
            UpdatePhase.Download => "下载",
            UpdatePhase.Verify => "校验",
            UpdatePhase.Commit => "应用",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "未知的更新阶段"),
        };

        return new($"正在{verb} {candidate}：{wholePercent}%", (float)clamped, "更新中", StatusKind.Working, false);
    }

    public static UpdateStatusModel Completed(Version from, Version to) =>
        new(JustUpdatedStatusBar(from, to), 1F, "完成", StatusKind.Good, true);

    public static UpdateStatusModel Postponed(Version current, Version candidate) =>
        new($"已推迟更新到 {candidate}，下次启动再问。", 0F, "已推迟", StatusKind.Neutral, true);

    public static UpdateStatusModel Skipped(Version current, Version candidate) =>
        new($"已跳过版本 {candidate}。", 0F, "已跳过", StatusKind.Neutral, true);

    /// <summary>更新失败不能让主功能陪葬，所以检查按钮保持可用。</summary>
    public static UpdateStatusModel Failed(string stage, string message) =>
        new($"更新失败（{stage}）：{message}。程序可继续使用。", 0F, "失败", StatusKind.Bad, true);

    /// <summary>新版本启动后在状态栏显示「已从 X 更新到 Y」，不弹窗。</summary>
    public static string JustUpdatedStatusBar(Version? from, Version to) =>
        from is null ? $"已更新到 {to}" : $"已从 {from} 更新到 {to}";
}
