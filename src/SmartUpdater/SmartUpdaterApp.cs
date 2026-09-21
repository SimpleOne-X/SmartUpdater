using System.Runtime.Versioning;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 启动入口：崩溃恢复、清理上次更新残留、等待旧进程退出、检测安装目录可写性、报告启动状态。
/// </summary>
public static class SmartUpdaterApp
{
    private static string? s_lastAppId;
    private static string? s_lastRejectedInstanceName;

    internal static string? LastAppId => Volatile.Read(ref s_lastAppId);

    internal static string? LastRejectedInstanceName => Volatile.Read(ref s_lastRejectedInstanceName);

    /// <summary>
    /// 启动收尾。必须是 <c>Main</c> 的第一句；同步执行，除 AppId 非法外永不抛出。
    /// </summary>
    /// <param name="args">命令行参数（不含程序路径）；为 null 时回退到进程命令行。</param>
    /// <returns>本次启动的状态。</returns>
    public static StartupResult Run(string[] args) => RunCore(args, null, null);

    /// <summary>
    /// 启动收尾，显式指定 AppId（必须与 <c>UpdateClientOptions.AppId</c> 一致）。
    /// </summary>
    /// <param name="args">命令行参数（不含程序路径）；为 null 时回退到进程命令行。</param>
    /// <param name="appId">应用标识。</param>
    /// <returns>本次启动的状态。</returns>
    /// <exception cref="ArgumentException"><paramref name="appId"/> 不合法。</exception>
    public static StartupResult Run(string[] args, string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return RunCore(args, appId, null);
    }

    /// <summary>
    /// 启动收尾，显式指定 AppId 与本地数据根目录（与 <c>UpdateClientOptions.AppId</c> / <c>LocalAppDataDirectory</c> 对应）。
    /// 仅为可测试性提供：让下载缓存与日志回退目录落在临时目录而非真实 <c>%LOCALAPPDATA%</c>。
    /// </summary>
    /// <param name="args">命令行参数（不含程序路径）；为 null 时回退到进程命令行。</param>
    /// <param name="appId">应用标识；null 表示按入口程序集与主程序路径推导。</param>
    /// <param name="localAppDataDirectory">本地数据根目录；null 表示真实的 Known Folder，空白抛异常，目录不必存在。</param>
    /// <returns>本次启动的状态。</returns>
    /// <exception cref="ArgumentException"><paramref name="appId"/> 不合法，或 <paramref name="localAppDataDirectory"/> 是空白。</exception>
    public static StartupResult Run(string[] args, string? appId, string? localAppDataDirectory)
        => RunCore(args, appId, localAppDataDirectory);

    /// <summary>
    /// 通知已有实例：向最近一次 <see cref="StartupResult.TryAcquireSingleInstance(string)"/> 返回 null 时的那个实例发出激活请求。
    /// </summary>
    /// <returns>true 表示已通知；false 表示没有可通知的实例，或当前不是 Windows。</returns>
    public static bool ActivateExistingInstance()
    {
        string? name = LastRejectedInstanceName;
        return name is not null && ActivateInstance(name);
    }

    internal static void RememberRejectedInstance(string fullName) => Volatile.Write(ref s_lastRejectedInstanceName, fullName);

    internal static bool ActivateInstance(string fullName)
        => OperatingSystem.IsWindows() && ActivateWindowsInstance(fullName);

    [SupportedOSPlatform("windows")]
    private static bool ActivateWindowsInstance(string fullName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(fullName + SingleInstanceHandle.ActivationSuffix, out EventWaitHandle? handle))
            {
                return false;
            }

            using (handle)
            {
                handle.Set();
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StartupResult RunCore(string[]? args, string? appId, string? localAppDataDirectory)
    {
        var env = new UpdateEnvironment();
        var runner = new StartupRunner(env, appId, layout => CreateEngine(env, layout), localAppDataDirectory);
        StartupResult result = runner.Run(args);
        Volatile.Write(ref s_lastAppId, result.AppId);
        return result;
    }

    private static UpdateEngine CreateEngine(UpdateEnvironment env, UpdateLayout layout)
    {
        // 启动收尾不应用包，主程序相对路径只是占位；拿不到进程身份时不能解引用它。
        string mainExecutable = $"{env.EntryAssemblyName}.exe";
        try
        {
            ProcessIdentity identity = AppIdResolver.ResolveProcessIdentity(env, null);
            try
            {
                mainExecutable = identity.RelativeExecutablePath(layout.InstallDirectory);
            }
            catch (InvalidOperationException)
            {
                string fileName = Path.GetFileName(identity.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    mainExecutable = fileName;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 无进程路径：沿用占位值。
        }

        (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(layout, enableFileLogging: true, null, env.TimeProvider);
        return new UpdateEngine(layout, mainExecutable, env, log, fileLogger);
    }
}
