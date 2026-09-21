using System.Diagnostics;
using System.Globalization;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 重启协调：触发 <c>Restarting</c> 事件，有界等待消费方的收尾任务，再启动新进程。
/// 已过不可回头点：等待与启动都忽略取消。本类不碰 Mutex。
/// </summary>
internal sealed class RestartCoordinator
{
    private readonly ResolvedClientOptions _options;
    private readonly IUpdateEngine _engine;
    private readonly IUpdateEventSink _events;
    private readonly UpdateEnvironment _env;

    /// <param name="options">已解析的客户端选项。</param>
    /// <param name="engine">更新引擎（取日志与安装目录）。</param>
    /// <param name="events">事件出口。</param>
    /// <param name="env">环境依赖。</param>
    public RestartCoordinator(ResolvedClientOptions options, IUpdateEngine engine, IUpdateEventSink events, UpdateEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(env);

        _options = options;
        _engine = engine;
        _events = events;
        _env = env;
    }

    /// <summary>
    /// 触发 Restarting，在 ShutdownTimeout 内等待收尾任务，然后启动新进程。
    /// true = 已启动；false = 启动失败（已记 Error，调用方上报 Failed(Commit)）。
    /// 已过不可回头点：等待与启动都忽略 <paramref name="ct"/>。
    /// </summary>
    /// <param name="toVersion">更新后的版本（四段）。</param>
    /// <param name="ct">仅为接口对称保留；不可回头点之后不参与任何等待。</param>
    public async Task<bool> RestartAsync(Version toVersion, CancellationToken ct)
    {
        _ = ct;
        IUpdateLog log = _engine.Log;

        var args = new RestartingEventArgs(toVersion);
        await _events.RaiseRestartingAsync(args).ConfigureAwait(false);

        IReadOnlyList<Task> pending = args.PendingTasks;
        if (pending.Count > 0)
        {
            Task all = Task.WhenAll(pending);
            try
            {
                if (_options.ShutdownTimeout == TimeSpan.Zero)
                {
                    if (!all.IsCompleted)
                    {
                        throw new TimeoutException();
                    }

                    await all.ConfigureAwait(false);
                }
                else
                {
                    await all.WaitAsync(_options.ShutdownTimeout, _env.TimeProvider).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                log.Warning(UpdateStage.Commit, $"收尾任务在 {_options.ShutdownTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒内未完成，照常重启");
            }
            catch (Exception ex)
            {
                log.Warning(UpdateStage.Commit, "收尾任务出错，照常重启", ex);
            }
        }

        string arguments = BuildArguments(_options.Identity, toVersion, _env.ProcessId);
        string fileName = _options.Identity.LaunchFileName;
        try
        {
            int pid = _env.ProcessLauncher.Start(fileName, arguments, _engine.Layout.InstallDirectory);
            log.Information(UpdateStage.Commit, $"已启动新进程 {pid.ToString(CultureInfo.InvariantCulture)}：{fileName} {arguments}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(UpdateStage.Commit, $"启动新进程失败：{fileName} {arguments}", ex);
            return false;
        }
    }

    /// <summary>
    /// 构造新进程命令行：<c>&lt;prefix&gt; --smartupdater-updated &lt;ver&gt; --smartupdater-wait-pid &lt;pid&gt;</c>（前缀为空时无前导空格）。
    /// </summary>
    /// <param name="identity">进程身份（提供启动前缀）。</param>
    /// <param name="toVersion">更新后的版本（必须是四段）。</param>
    /// <param name="currentProcessId">当前进程 PID，交给新进程等待。</param>
    public static string BuildArguments(ProcessIdentity identity, Version toVersion, int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(toVersion);
        Debug.Assert(VersionNormalization.IsCanonical(toVersion), "toVersion 必须是四段版本");

        string tail = CommandLine.Build(
        [
            SmartUpdaterArgs.Updated,
            toVersion.ToString(),
            SmartUpdaterArgs.WaitPid,
            currentProcessId.ToString(CultureInfo.InvariantCulture),
        ]);

        return identity.LaunchArgumentPrefix.Length == 0
            ? tail
            : identity.LaunchArgumentPrefix + " " + tail;
    }
}
