using System.Reflection;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 全部环境依赖的注入点。缺省值即真实环境；测试按需覆盖。
/// <see cref="CaptureSynchronizationContext"/> 在 UpdateClient 构造时调用一次。
/// </summary>
internal sealed class UpdateEnvironment
{
    public string InstallDirectory { get; init; } = AppContext.BaseDirectory;

    public string LocalApplicationDataDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? ProcessPath { get; init; } = Environment.ProcessPath;

    /// <summary>[0] 是宿主或程序集路径。</summary>
    public string[] CommandLineArguments { get; init; } = Environment.GetCommandLineArgs();

    public string EntryAssemblyName { get; init; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "app";

    public Version? EntryAssemblyVersion { get; init; } = Assembly.GetEntryAssembly()?.GetName().Version;

    public int ProcessId { get; init; } = Environment.ProcessId;

    public string MachineName { get; init; } = Environment.MachineName;

    public string OsVersion { get; init; } = Environment.OSVersion.VersionString;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Random Random { get; init; } = Random.Shared;

    public IProcessLauncher ProcessLauncher { get; init; } = SimpleOneX.SmartUpdater.ProcessLauncher.Instance;

    public IProcessWaiter ProcessWaiter { get; init; } = SimpleOneX.SmartUpdater.ProcessWaiter.Instance;

    public IFileOperations FileOperations { get; init; } = PhysicalFileOperations.Instance;

    public Func<SynchronizationContext?> CaptureSynchronizationContext { get; init; } = static () => SynchronizationContext.Current;
}
