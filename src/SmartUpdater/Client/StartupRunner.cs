namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 启动收尾的实现（<see cref="SmartUpdaterApp.Run(string[])"/> 的内核）。
/// 顺序：先等旧进程退出，再做崩溃恢复；任何意外都降级为"更新不可用"，不影响程序启动。
/// </summary>
internal sealed class StartupRunner
{
    public static readonly TimeSpan WaitForOldProcessTimeout = TimeSpan.FromSeconds(30);

    private readonly UpdateEnvironment _env;
    private readonly string? _appIdOverride;
    private readonly Func<UpdateLayout, IUpdateEngine> _engineFactory;
    private readonly string? _localAppDataDirectory;

    public StartupRunner(UpdateEnvironment env, string? appIdOverride, Func<UpdateLayout, IUpdateEngine> engineFactory, string? localAppDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(engineFactory);

        _env = env;
        _appIdOverride = appIdOverride;
        _engineFactory = engineFactory;
        _localAppDataDirectory = localAppDataDirectory;
    }

    /// <exception cref="ArgumentException">显式 AppId 不合法（程序员错误）。</exception>
    public StartupResult Run(string[]? args)
    {
        args ??= [.. _env.CommandLineArguments.Skip(1)];

        ProcessIdentity? identity = null;
        try
        {
            identity = AppIdResolver.ResolveProcessIdentity(_env, null);
        }
        catch (InvalidOperationException)
        {
            // 拿不到 exe 路径不能让程序起不来：AppId 改用安装目录推导，警告在拿到日志后补记。
        }

        string appId;
        if (_appIdOverride is not null)
        {
            AppIdResolver.Validate(_appIdOverride);
            appId = _appIdOverride;
        }
        else if (identity is not null)
        {
            appId = AppIdResolver.ResolveAppId(_env, null, identity);
        }
        else
        {
            appId = AppIdentity.Derive(_env.EntryAssemblyName, _env.InstallDirectory);
        }

        string localAppData = ResolvedClientOptions.ResolveLocalAppData(_localAppDataDirectory, _env.LocalApplicationDataDirectory, "localAppDataDirectory");
        var layout = new UpdateLayout(_env.InstallDirectory, localAppData, appId);
        IUpdateEngine engine = _engineFactory(layout);

        Version fallbackVersion = VersionNormalization.Canonical(_env.EntryAssemblyVersion ?? VersionNormalization.Zero);
        var outcome = new Outcome(fallbackVersion);
        try
        {
            if (identity is null)
            {
                engine.Log.Warning(null, $"无法确定主程序路径，AppId 改由安装目录 {_env.InstallDirectory} 推导");
            }

            Execute(engine, args, outcome);
        }
        catch (Exception ex)
        {
            engine.Log.Error(null, "启动收尾失败", ex);
            outcome.IsUpdateEnabled = false;
            outcome.UpdateDisabledReason = ex.Message;
        }
        finally
        {
            engine.Dispose();
        }

        return new StartupResult(
            outcome.JustUpdated,
            outcome.FromVersion,
            outcome.ToVersion,
            outcome.CurrentVersion,
            appId,
            outcome.IsUpdateEnabled,
            outcome.UpdateDisabledReason,
            outcome.IsRollbackApplied);
    }

    private static Version? CanonicalOrNull(Version? version) => version is null ? null : VersionNormalization.Canonical(version);

    private void Execute(IUpdateEngine engine, string[] args, Outcome outcome)
    {
        IUpdateLog log = engine.Log;
        UpdateState state = engine.LoadState();

        WaitForOldProcess(args, log);
        Version? updatedArg = ReadUpdatedArgument(args, log);

        if (CommandLine.HasFlag(args, SmartUpdaterArgs.Rollback))
        {
            ApplyRollback(engine, outcome);
        }
        else
        {
            RunRecovery(engine, state, updatedArg, outcome);
        }

        InstallDirectoryProbeResult probe = engine.ProbeInstallDirectory();
        if (!probe.IsWritable)
        {
            outcome.IsUpdateEnabled = false;
            outcome.UpdateDisabledReason = probe.FailureReason;
            log.Error(UpdateStage.PermissionCheck, $"安装目录不可写，更新已禁用：{probe.FailureReason}");
            Enqueue(engine, state, UpdateStage.PermissionCheck, probe.FailureReason ?? "安装目录不可写", null, null);
        }

        ResolveCurrentVersion(engine, outcome);
    }

    private void WaitForOldProcess(string[] args, IUpdateLog log)
    {
        if (!CommandLine.HasFlag(args, SmartUpdaterArgs.WaitPid))
        {
            return;
        }

        string? text = CommandLine.GetOptionValue(args, SmartUpdaterArgs.WaitPid);
        if (text is null)
        {
            log.Warning(null, $"{SmartUpdaterArgs.WaitPid} 缺少值，忽略");
            return;
        }

        if (!int.TryParse(text, out int pid) || pid <= 0)
        {
            log.Warning(null, $"{SmartUpdaterArgs.WaitPid} 的值 \"{text}\" 不是有效的进程号，忽略");
            return;
        }

        if (_env.ProcessWaiter.WaitForExit(pid, WaitForOldProcessTimeout))
        {
            log.Information(null, $"旧进程 {pid} 已退出");
        }
        else
        {
            log.Warning(null, $"旧进程 {pid} 在 {(int)WaitForOldProcessTimeout.TotalSeconds} s 内未退出，继续启动");
        }
    }

    private static Version? ReadUpdatedArgument(string[] args, IUpdateLog log)
    {
        if (!CommandLine.HasFlag(args, SmartUpdaterArgs.Updated))
        {
            return null;
        }

        string? text = CommandLine.GetOptionValue(args, SmartUpdaterArgs.Updated);
        if (text is null)
        {
            log.Warning(null, $"{SmartUpdaterArgs.Updated} 缺少值，忽略");
            return null;
        }

        if (!VersionNormalization.TryParse(text, out Version? version))
        {
            log.Warning(null, $"{SmartUpdaterArgs.Updated} 的值 \"{text}\" 不是有效的版本号，忽略");
            return null;
        }

        return version;
    }

    private void ApplyRollback(IUpdateEngine engine, Outcome outcome)
    {
        IUpdateLog log = engine.Log;
        RecoveryResult rollback = engine.RollbackFromBackups();
        engine.DeleteJournal();

        UpdateState state = engine.LoadState();
        state.CurrentVersion = engine.ReadInstalledVersion() ?? state.CurrentVersion;
        TrySaveState(engine, state);

        outcome.IsRollbackApplied = true;
        ReportErrors(engine, state, rollback.Errors, "回滚");
    }

    private void RunRecovery(IUpdateEngine engine, UpdateState state, Version? updatedArg, Outcome outcome)
    {
        IUpdateLog log = engine.Log;
        RecoveryResult recovery = engine.RunRecovery();

        ReportErrors(engine, state, recovery.Errors, "恢复");

        if (recovery.JustUpdated)
        {
            outcome.JustUpdated = true;
            outcome.FromVersion = CanonicalOrNull(recovery.FromVersion);
            outcome.ToVersion = CanonicalOrNull(recovery.ToVersion) ?? updatedArg;

            if (outcome.ToVersion is null)
            {
                log.Warning(null, "更新已完成，但 journal 与启动参数都没有给出新版本号");
            }
            else
            {
                Enqueue(engine, state, outcome.FromVersion, outcome.ToVersion);
            }

            if (updatedArg is not null && updatedArg != outcome.ToVersion)
            {
                log.Warning(null, $"{SmartUpdaterArgs.Updated} 的值 {updatedArg} 与 journal 记录的 {outcome.ToVersion} 不一致，以 journal 为准");
            }
        }
        else if (updatedArg is not null)
        {
            log.Information(null, $"启动参数带 {SmartUpdaterArgs.Updated} 但没有 done journal，忽略");
        }
    }

    private void ResolveCurrentVersion(IUpdateEngine engine, Outcome outcome)
    {
        UpdateState state = engine.LoadState();
        Version? source = state.CurrentVersion ?? engine.ReadInstalledVersion() ?? _env.EntryAssemblyVersion;
        Version current = VersionNormalization.Canonical(source ?? VersionNormalization.Zero);
        outcome.CurrentVersion = current;

        if (source is null)
        {
            engine.Log.Warning(null, "无法确定当前版本（状态、manifest、程序集版本都没有），按 0.0.0.0 处理");
            return;
        }

        if (state.CurrentVersion is null)
        {
            state.CurrentVersion = current;
            TrySaveState(engine, state);
        }
    }

    private static void TrySaveState(IUpdateEngine engine, UpdateState state)
    {
        try
        {
            engine.SaveState(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            engine.Log.Warning(null, $"保存状态失败：{ex.Message}", ex);
        }
    }

    private void ReportErrors(IUpdateEngine engine, UpdateState state, IReadOnlyList<string> errors, string what)
    {
        if (errors.Count == 0)
        {
            return;
        }

        foreach (string error in errors)
        {
            engine.Log.Warning(UpdateStage.Rollback, $"{what}过程中的问题：{error}");
        }

        Enqueue(engine, state, UpdateStage.Rollback, string.Join("; ", errors), null, null);
    }

    private void Enqueue(IUpdateEngine engine, UpdateState state, Version? from, Version to)
        => engine.EnqueueReport(UpdateReportBuilder.Updated(
            state.DeviceGuid, _env, from, to, engine.GetAvailableDiskBytes(), _env.TimeProvider.GetUtcNow()));

    private void Enqueue(IUpdateEngine engine, UpdateState state, UpdateStage stage, string message, Version? from, Version? to)
        => engine.EnqueueReport(UpdateReportBuilder.Failed(
            state.DeviceGuid, _env, stage, message, from, to, 0, engine.GetAvailableDiskBytes(), engine.ReadLogTail(), _env.TimeProvider.GetUtcNow()));

    private sealed class Outcome(Version currentVersion)
    {
        public bool JustUpdated { get; set; }

        public Version? FromVersion { get; set; }

        public Version? ToVersion { get; set; }

        public Version CurrentVersion { get; set; } = currentVersion;

        public bool IsUpdateEnabled { get; set; } = true;

        public string? UpdateDisabledReason { get; set; }

        public bool IsRollbackApplied { get; set; }
    }
}
