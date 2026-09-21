using System.Globalization;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 更新客户端：启动时立即检查一次，然后按轮询间隔检查更新；周期性心跳与补报。
/// 更新完成并启动新进程后 <see cref="RunAsync"/> 正常结束，消费方据此退出当前进程。
/// 事件在构造本对象时捕获的 <see cref="SynchronizationContext"/> 上触发（没有则在后台线程触发）。
/// </summary>
public sealed class UpdateClient : IDisposable, IUpdateEventSink
{
    private readonly UpdateEnvironment _env;
    private readonly ResolvedClientOptions _resolved;
    private readonly IUpdateEngine _engine;
    private readonly IUpdateLog _log;
    private readonly EventDispatcher _dispatcher;
    private readonly TransportSet _transports;
    private readonly UpdateCycle _cycle;
    private readonly RestartCoordinator _restart;

    private int _runState;
    private int _disposed;
    private volatile bool _updatesEnabled = true;

    /// <summary>最简用法：等价于 <c>new UpdateClient(new UpdateClientOptions { FeedUrl = feedUrl })</c>。</summary>
    /// <param name="feedUrl">feed（releases.json）的绝对 http / https 地址。</param>
    /// <exception cref="ArgumentException"><paramref name="feedUrl"/> 不合法。</exception>
    public UpdateClient(string feedUrl)
        : this(new UpdateClientOptions { FeedUrl = feedUrl })
    {
    }

    /// <summary>
    /// 校验选项、捕获当前 <see cref="SynchronizationContext"/>，并与 <c>SmartUpdaterApp.Run</c> 使用的 AppId 做一致性检查。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">选项不合法。</exception>
    /// <exception cref="InvalidOperationException"><see cref="UpdateClientOptions.AppId"/> 与 <c>SmartUpdaterApp.Run</c> 使用的 AppId 不一致。</exception>
    public UpdateClient(UpdateClientOptions options)
        : this(options, new UpdateEnvironment(), null, null)
    {
    }

    /// <summary>测试缝：注入环境、引擎与传输。传入 <paramref name="engine"/> 时不创建任何日志。</summary>
    internal UpdateClient(UpdateClientOptions options, UpdateEnvironment environment, IUpdateEngine? engine = null, TransportSet? transports = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _env = environment;
        _resolved = ResolvedClientOptions.From(options, _env);
        string appId = ResolveEffectiveAppId(options.AppId, SmartUpdaterApp.LastAppId, _resolved.AppId);
        UpdateLayout layout = CreateLayout(_resolved, _env, appId);

        bool ownsEngine = engine is null;
        if (engine is null)
        {
            (IUpdateLog log, FileLogger? fileLogger) = UpdateLogFactory.Create(layout, options.EnableFileLogging, options.LogCallback, _env.TimeProvider);
            engine = new UpdateEngine(layout, ResolveMainExecutable(layout, log), _env, log, fileLogger);
        }

        _engine = engine;
        _log = engine.Log;

        try
        {
            _dispatcher = new EventDispatcher(_env.CaptureSynchronizationContext(), _log);
            _transports = transports ?? TransportFactory.Create(_resolved, _log, _env.TimeProvider);
            _cycle = new UpdateCycle(_resolved, _transports.Feed, _transports.Downloader, _engine, this, _env);
            _restart = new RestartCoordinator(_resolved, _engine, this, _env);
        }
        catch
        {
            if (ownsEngine)
            {
                _engine.Dispose();
            }

            throw;
        }
    }

    /// <summary>发现可选更新时触发；处理器返回前必须通过参数表态（安装 / 跳过 / 推迟）。</summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    /// <summary>下载与应用进度。</summary>
    public event EventHandler<UpdateProgressEventArgs>? ProgressChanged;

    /// <summary>即将启动新进程、重启前触发；处理器可登记收尾任务。</summary>
    public event EventHandler<RestartingEventArgs>? Restarting;

    /// <summary>某个阶段失败时触发。</summary>
    public event EventHandler<UpdateFailedEventArgs>? Failed;

    /// <summary>当前生效的运行参数（测试观察）。</summary>
    internal ResolvedClientSettings CurrentSettings => _updatesEnabled ? _cycle.Settings : _resolved.LocalSettings;

    /// <summary>
    /// 启动时立即检查一次，然后按轮询间隔检查；同时做周期性心跳与补报。
    /// 只在新进程已启动时正常结束（消费方据此退出）；取消 → <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">已有一个 RunAsync 在运行。</exception>
    /// <exception cref="OperationCanceledException">已取消。</exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            throw new InvalidOperationException("RunAsync 已在运行");
        }

        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _runState, 0);
        }
    }

    /// <summary>释放本包自建的 HttpClient 与日志。幂等；调用前由消费方先取消正在运行的 <see cref="RunAsync"/>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transports.Dispose();
        _engine.Dispose();
    }

    /// <summary><c>UpdateClientOptions.AppId</c> 与 <c>SmartUpdaterApp.Run</c> 的 AppId 都显式给出时必须一致。</summary>
    internal static string ResolveEffectiveAppId(string? optionsAppId, string? lastRunAppId, string derivedAppId)
    {
        if (optionsAppId is not null && lastRunAppId is not null && !string.Equals(optionsAppId, lastRunAppId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UpdateClientOptions.AppId 与 SmartUpdaterApp.Run 使用的 AppId 不一致");
        }

        return optionsAppId ?? lastRunAppId ?? derivedAppId;
    }

    internal static UpdateLayout CreateLayout(ResolvedClientOptions resolved, UpdateEnvironment env, string appId)
        => new(env.InstallDirectory, resolved.LocalAppDataDirectory, appId);

    /// <inheritdoc />
    bool IUpdateEventSink.HasUpdateAvailableSubscribers => UpdateAvailable is not null;

    /// <inheritdoc />
    async Task IUpdateEventSink.RaiseUpdateAvailableAsync(UpdateAvailableEventArgs args)
    {
        await _dispatcher.InvokeAsync(UpdateAvailable, this, args).ConfigureAwait(false);
        args.Seal();
    }

    /// <inheritdoc />
    void IUpdateEventSink.RaiseProgress(UpdateProgressEventArgs args) => _dispatcher.Post(ProgressChanged, this, args);

    /// <inheritdoc />
    Task IUpdateEventSink.RaiseFailedAsync(UpdateFailedEventArgs args) => _dispatcher.InvokeAsync(Failed, this, args);

    /// <inheritdoc />
    async Task IUpdateEventSink.RaiseRestartingAsync(RestartingEventArgs args)
    {
        await _dispatcher.InvokeAsync(Restarting, this, args).ConfigureAwait(false);
        args.Seal();
    }

    private string ResolveMainExecutable(UpdateLayout layout, IUpdateLog log)
    {
        try
        {
            return _resolved.Identity.RelativeExecutablePath(layout.InstallDirectory);
        }
        catch (InvalidOperationException ex)
        {
            string fallback = Path.GetFileName(_resolved.Identity.ExecutablePath);
            log.Warning(null, $"主程序不在安装目录下，改用文件名 {fallback}", ex);
            return fallback;
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        LogStartup();

        InstallDirectoryProbeResult probe = _engine.ProbeInstallDirectory();
        bool updatesEnabled = probe.IsWritable;
        _updatesEnabled = updatesEnabled;
        if (!updatesEnabled)
        {
            // 不入队：SmartUpdaterApp.Run 已经入队；程序照常运行，本循环只做心跳与补报。
            _log.Error(UpdateStage.PermissionCheck, $"安装目录不可写，更新已禁用：{probe.FailureReason}");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (updatesEnabled)
            {
                CycleOutcome outcome = await _cycle.RunAsync(ct).ConfigureAwait(false);
                if (outcome.Kind == CycleOutcomeKind.Installed)
                {
                    Version target = outcome.TargetVersion!;
                    bool launched = await _restart.RestartAsync(target, ct).ConfigureAwait(false);
                    if (launched)
                    {
                        await TryFlushAfterLaunchAsync(ct).ConfigureAwait(false);
                        _log.Information(UpdateStage.Commit, "新进程已启动，RunAsync 结束");
                        return;
                    }

                    await ReportLaunchFailureAsync(target).ConfigureAwait(false);
                }
            }

            ResolvedClientSettings settings = updatesEnabled ? _cycle.Settings : _resolved.LocalSettings;
            TimeSpan? untilHeartbeat = await HeartbeatIfDueAsync(settings, ct).ConfigureAwait(false);
            await FlushAsync(ct).ConfigureAwait(false);

            TimeSpan wait = untilHeartbeat is { } until && until < settings.PollInterval ? until : settings.PollInterval;
            TimeSpan delay = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, wait.Ticks));
            await Task.Delay(delay, _env.TimeProvider, ct).ConfigureAwait(false);
        }
    }

    private void LogStartup()
    {
        UpdateClientOptions source = _resolved.Source;
        ResolvedClientSettings local = _resolved.LocalSettings;
        string feed = _resolved.FeedUri?.ToString() ?? source.Feed?.GetType().Name ?? "无";
        _log.Information(
            null,
            $"UpdateClient 启动：AppId={_engine.Layout.AppId}，feed={feed}，"
            + $"轮询 {local.PollInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒，"
            + $"抖动窗口 {local.JitterWindow.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒，"
            + $"心跳 {local.HeartbeatInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒，"
            + $"允许降级={source.AllowVersionDowngrade}，验签公钥={(_resolved.PublicKey is null ? "未配置" : "已配置")}");

        if (source.AllowUntrustedCertificates)
        {
            _log.Warning(null, "AllowUntrustedCertificates 已开启：仅限内网自签名场景");
        }
    }

    private async Task ReportLaunchFailureAsync(Version target)
    {
        var failure = new UpdateFailedException(UpdateStage.Commit, "启动新版本失败，当前进程继续以旧版本运行；下次手动启动即为新版本");
        await ((IUpdateEventSink)this).RaiseFailedAsync(new UpdateFailedEventArgs(failure.Stage, failure, target)).ConfigureAwait(false);

        UpdateState state = _engine.LoadState();
        _engine.EnqueueReport(UpdateReportBuilder.Failed(
            state.DeviceGuid,
            _env,
            failure.Stage,
            failure.Message,
            null,
            target,
            0,
            _engine.GetAvailableDiskBytes(),
            _resolved.Source.IncludeLogTailOnFailure ? _engine.ReadLogTail() : null,
            _env.TimeProvider.GetUtcNow()));
    }

    /// <summary>
    /// 心跳到期就入队一条（跨重启按 <c>LastReportedAt</c> 计）。返回距下一次心跳的时间；没有上报通道时返回 null。
    /// </summary>
    private async Task<TimeSpan?> HeartbeatIfDueAsync(ResolvedClientSettings settings, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (_transports.Reporter is null)
        {
            _log.Debug(null, "未配置上报通道，跳过心跳");
            return null;
        }

        UpdateState state = _engine.LoadState();
        DateTimeOffset now = _env.TimeProvider.GetUtcNow();
        if (state.LastReportedAt is null || now - state.LastReportedAt.Value >= settings.HeartbeatInterval)
        {
            Version current = VersionNormalization.Canonical(state.CurrentVersion ?? VersionNormalization.Zero);
            _engine.EnqueueReport(UpdateReportBuilder.Heartbeat(state.DeviceGuid, _env, current, _engine.GetAvailableDiskBytes(), now));
            state.LastReportedAt = now;
            try
            {
                _engine.SaveState(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(null, "保存心跳时间失败", ex);
            }
        }

        return state.LastReportedAt.Value + settings.HeartbeatInterval - now;
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        IUpdateReporter? reporter = _transports.Reporter;
        if (reporter is null)
        {
            return;
        }

        try
        {
            await _engine.FlushReportsAsync(reporter, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(null, "补报失败，稍后重试", ex);
        }
    }

    /// <summary>新进程已启动后的收尾冲刷：尽力而为，取消也不影响"已启动"的结局。</summary>
    private async Task TryFlushAfterLaunchAsync(CancellationToken ct)
    {
        try
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Debug(UpdateStage.Commit, "新进程已启动，收尾冲刷被取消");
        }
    }
}
