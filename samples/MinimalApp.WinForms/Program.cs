using System.Drawing.Text;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // SmartUpdaterApp.Run 必须先于任何 UI 初始化与单实例检查。
        // 它前面只允许有"纯参数解析"（不碰文件、进程、UI）——这里取出命令行里的两个选项。
        // 更新后重启出来的进程只会收到 --smartupdater-* 两个开关，钩子选项从环境变量补回来（只读一个环境变量，
        // 不碰文件/进程/UI，仍属"纯参数解析"）。真实用户没传钩子选项，变量不存在，hookArgs 与 args 完全相同。
        string[] hookArgs = E2eHookOptions.ResolveArguments(args, Environment.GetEnvironmentVariable);
        E2eHookOptions options = E2eHookOptions.Parse(hookArgs);
        StartupResult startup = SmartUpdaterApp.Run(hookArgs, null, options.LocalAppDataDirectory);

        // 非 AOT 交付要在启动早期预热会被替换的程序集（见 Preheat）。
        // 位置：Run 之后、创建窗口之前。
        int preheated = Preheat.LoadInstallDirectoryAssemblies();

        // 自动化钩子（默认全部关闭）：放在单实例检查之前，这样被拒绝的第二个实例也能留下 started 事件。
        E2eHooks.Initialize(hookArgs, startup, preheated);

        // using 形式，代码里不出现任何 ReleaseMutex。
        using SingleInstanceHandle? instance = startup.TryAcquireSingleInstance("MinimalApp.WinForms");
        if (instance is null)
        {
            E2eHooks.Log("exiting", ("reason", "single-instance-rejected"));
            SmartUpdaterApp.ActivateExistingInstance();
            return;
        }

        // ① 进程级 WinForms 设置：必须在创建任何窗口 / 控件之前。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));

        // ② AntdUI 全局配置：同样在第一个 AntdUI 窗口创建之前。
        AntdUI.Config.UseHook = false;                  // Release 下默认 true，会装进程内全局低级键鼠钩子
        AntdUI.Config.TextRenderingHighQuality = true;
        AntdUI.Config.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        AntdUI.Config.SetCorrectionTextRendering("Microsoft YaHei UI");
        AntdUI.Config.Theme().Light("#ffffff", "#000000").Dark("#141414", "#ffffff").FormBorderColor();

        var window = new MainWindow(startup, instance, E2eHooks.CreateDriverOptions());
        E2eHooks.Attach(window);
        Application.Run(window);
    }
}
