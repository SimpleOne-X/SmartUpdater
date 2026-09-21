using System.Globalization;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>一轮"检测到提交"的结局。</summary>
internal enum CycleOutcomeKind
{
    /// <summary>本轮无可安装版本（含 304 且缓存文档无更新、已是最新、不允许降级、无可达条目）。</summary>
    NoUpdate,

    /// <summary>optional：消费方 Postpone / 未表态 / 本进程已 Postpone 过。</summary>
    Postponed,

    /// <summary>optional：消费方 Skip（已持久化）。</summary>
    Skipped,

    /// <summary>连续跳板达到上限：已上报（或因 feed 未变而静默）。</summary>
    ChainLimitReached,

    /// <summary>提交完成，需要重启。</summary>
    Installed,

    /// <summary>某阶段失败：已触发 Failed 事件并入队上报。</summary>
    Failed,
}

/// <summary>一轮的结果。</summary>
/// <param name="Kind">结局。</param>
/// <param name="TargetVersion">目标版本（四段）；尚未选出时为 null。</param>
/// <param name="Settings">本轮生效的运行参数。</param>
/// <param name="Failure">失败原因；仅 <see cref="CycleOutcomeKind.Failed"/> 与已上报的 <see cref="CycleOutcomeKind.ChainLimitReached"/> 非 null。</param>
internal sealed record CycleOutcome(CycleOutcomeKind Kind, Version? TargetVersion, ResolvedClientSettings Settings, UpdateFailedException? Failure);

/// <summary>单次"检测 → 提交"的状态机。取消原样抛出，其他失败全部转成 <see cref="CycleOutcomeKind.Failed"/>（已触发事件、已入队上报）。</summary>
internal sealed class UpdateCycle
{
    private readonly ResolvedClientOptions _options;
    private readonly IReleaseFeed _feed;
    private readonly IPackageDownloader _downloader;
    private readonly IUpdateEngine _engine;
    private readonly IUpdateEventSink _events;
    private readonly UpdateEnvironment _env;
    private readonly HashSet<Version> _postponed = [];

    private ValidatedFeed? _cachedFeed;

    public UpdateCycle(
        ResolvedClientOptions options,
        IReleaseFeed feed,
        IPackageDownloader downloader,
        IUpdateEngine engine,
        IUpdateEventSink events,
        UpdateEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(env);
        _options = options;
        _feed = feed;
        _downloader = downloader;
        _engine = engine;
        _events = events;
        _env = env;
        Settings = options.LocalSettings;
    }

    /// <summary>最近一次解析出的运行参数（初值为本地设置；拿到 feed 后覆盖并钳制）。</summary>
    public ResolvedClientSettings Settings { get; private set; }

    /// <summary>本进程内 Postpone 过的版本（四段）。</summary>
    public IReadOnlySet<Version> PostponedVersions => _postponed;

    private IUpdateLog Log => _engine.Log;

    /// <summary>跑一轮。取消 → OperationCanceledException；其他一切失败都转成 Failed，不抛。</summary>
    public async Task<CycleOutcome> RunAsync(CancellationToken ct)
    {
        long started = _env.TimeProvider.GetTimestamp();
        UpdateState state = _engine.LoadState();
        UpdateStage stage = UpdateStage.Check;
        Version? current = null;
        Version? targetVersion = null;
        bool chainLimit = false;
        UpdateFailedException failure;
        Exception original;

        try
        {
            // 1 Check
            ValidatedFeed validated = await FetchFeedAsync(state, ct).ConfigureAwait(false);

            // 1b
            ResolvedClientSettings resolved = ClientPolicyResolver.Resolve(validated.Document.Client, _options.LocalSettings);
            if (resolved != Settings)
            {
                Log.Information(
                    UpdateStage.Check,
                    $"feed 下发 pollIntervalSeconds={validated.Document.Client?.PollIntervalSeconds?.ToString(CultureInfo.InvariantCulture) ?? "无"}，生效 {resolved.PollInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒；"
                    + $"抖动窗口 {resolved.JitterWindow.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒，心跳 {resolved.HeartbeatInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒");
            }

            Settings = resolved;
            state.LastCheckedAt = _env.TimeProvider.GetUtcNow();
            TrySave(state);

            // 2 Select
            stage = UpdateStage.Check;
            current = ResolveCurrentVersion(state, warn: true);
            if (state.CurrentVersion is null)
            {
                state.CurrentVersion = current;
                TrySave(state);
            }

            Version currentVersion = current;
            HashSet<Version> skipped = state.GetSkippedVersionsForSelection()
                .Where(v => v is not null)
                .Select(VersionNormalization.Canonical)
                .Where(v => v != currentVersion)
                .ToHashSet();

            SelectionResult selection = ReleaseSelector.Explain(
                validated.Document,
                new SelectionContext(currentVersion, state.DeviceGuid, skipped, _options.Source.AllowVersionDowngrade));
            LogSelection(validated.Document, currentVersion, selection);

            // 2b
            if (selection.Selected is null)
            {
                if (state.ChainLength != 0)
                {
                    state.ChainLength = 0;
                    TrySave(state);
                }

                return new CycleOutcome(CycleOutcomeKind.NoUpdate, null, Settings, null);
            }

            ReleaseEntry target = selection.Selected;
            targetVersion = target.Version;

            // 2c 熔断
            if (state.ChainLimitFeedETag is not null)
            {
                if (state.FeedETag is not null && state.ChainLimitFeedETag == state.FeedETag)
                {
                    Log.Information(UpdateStage.Check, "feed 未变，跳板熔断仍生效");
                    return new CycleOutcome(CycleOutcomeKind.ChainLimitReached, targetVersion, Settings, null);
                }

                state.ChainLimitFeedETag = null;
                TrySave(state);
            }

            if (state.ChainLength >= ReleaseSelector.MaxChainLength)
            {
                state.ChainLength = 0;
                state.ChainLimitFeedETag = state.FeedETag;
                TrySave(state);
                chainLimit = true;
                throw new UpdateFailedException(
                    UpdateStage.Check,
                    $"连续跳板升级已达 {ReleaseSelector.MaxChainLength} 次（本次目标 {target.Version}），放弃本轮；feed 可能形成了 minUpdatableFrom 环");
            }

            // 3 决策
            ReleaseEntry raw = validated.RawOf(target);
            bool userAccepted = false;
            if (target.Mode == UpdateMode.Optional)
            {
                if (_postponed.Contains(target.Version))
                {
                    Log.Information(UpdateStage.Check, $"版本 {target.Version} 本进程内已选择稍后，不再询问");
                    return new CycleOutcome(CycleOutcomeKind.Postponed, targetVersion, Settings, null);
                }

                if (!_events.HasUpdateAvailableSubscribers)
                {
                    Log.Information(UpdateStage.Check, "无 UpdateAvailable 订阅者，按接受处理");
                }
                else
                {
                    var args = new UpdateAvailableEventArgs(target, currentVersion);
                    await _events.RaiseUpdateAvailableAsync(args).ConfigureAwait(false);
                    switch (args.Decision)
                    {
                        case UpdateDecision.Accept:
                            userAccepted = true;
                            break;
                        case UpdateDecision.Skip:
                            if (state.TrySkipVersion(target.Version))
                            {
                                TrySave(state);
                            }

                            Log.Information(UpdateStage.Check, $"使用者跳过版本 {target.Version}");
                            return new CycleOutcome(CycleOutcomeKind.Skipped, targetVersion, Settings, null);
                        case UpdateDecision.Undecided:
                            Log.Warning(UpdateStage.Check, "处理器未表态，按 Postpone 处理");
                            _postponed.Add(target.Version);
                            return new CycleOutcome(CycleOutcomeKind.Postponed, targetVersion, Settings, null);
                        default:
                            _postponed.Add(target.Version);
                            Log.Information(UpdateStage.Check, $"使用者选择稍后更新版本 {target.Version}");
                            return new CycleOutcome(CycleOutcomeKind.Postponed, targetVersion, Settings, null);
                    }
                }
            }

            // 3b 抖动
            if (!userAccepted && Settings.JitterWindow > TimeSpan.Zero)
            {
                var delay = TimeSpan.FromTicks(_env.Random.NextInt64(Settings.JitterWindow.Ticks));
                Log.Information(UpdateStage.Check, $"抖动等待 {delay.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒后开始下载");
                await DelayExactAsync(delay, ct).ConfigureAwait(false);
            }

            // 4 Download
            stage = UpdateStage.Download;
            string path = _engine.Layout.GetDownloadPath(target.Version);
            var downloadProgress = new ProgressRelay<DownloadProgress>(p =>
                _events.RaiseProgress(new UpdateProgressEventArgs(UpdateStage.Download, p.BytesReceived, p.TotalBytes, p.BytesPerSecond)));
            long downloadStart = _env.TimeProvider.GetTimestamp();
            string downloaded = await _downloader.DownloadAsync(raw, path, downloadProgress, ct).ConfigureAwait(false);
            TimeSpan downloadElapsed = _env.TimeProvider.GetElapsedTime(downloadStart);
            double seconds = downloadElapsed.TotalSeconds;
            string speed = seconds > 0
                ? $"{(raw.Package.Size / seconds).ToString("F0", CultureInfo.InvariantCulture)} 字节/秒"
                : "未知";
            Log.Information(
                UpdateStage.Download,
                $"下载 {raw.Package.Url}，{raw.Package.Size.ToString(CultureInfo.InvariantCulture)} 字节，耗时 {seconds.ToString("F1", CultureInfo.InvariantCulture)} 秒，平均 {speed}");

            // 5 Verify
            stage = UpdateStage.Verify;
            PackageSummary summary;
            try
            {
                var hashed = new ProgressRelay<long>(n =>
                    _events.RaiseProgress(new UpdateProgressEventArgs(UpdateStage.Verify, n, raw.Package.Size, 0)));
                await Task.Run(() => PackageVerifier.Verify(raw, downloaded, _options.PublicKey, Log, hashed, ct), ct).ConfigureAwait(false);
                summary = _engine.ReadPackage(downloaded);
            }
            catch (UpdateFailedException)
            {
                TryDelete(downloaded);
                throw;
            }

            if (VersionNormalization.Canonical(summary.Version) != target.Version)
            {
                TryDelete(downloaded);
                throw new UpdateFailedException(
                    UpdateStage.Verify,
                    $"包内 manifest 版本 {summary.Version} 与 feed 条目 {target.Version} 不一致");
            }

            // 6 DiskCheck
            stage = UpdateStage.DiskCheck;
            DiskSpaceCheckResult check = _engine.CheckDiskSpace(raw.Package.Size, summary.TotalFileBytes);
            if (check.Install.AvailableBytes is null)
            {
                Log.Warning(UpdateStage.DiskCheck, "无法获知磁盘可用空间，跳过空间判断");
            }

            Log.Information(
                UpdateStage.DiskCheck,
                $"磁盘空间：需要 {check.RequiredBytes.ToString(CultureInfo.InvariantCulture)} 字节，可用 {check.Install.AvailableBytes?.ToString(CultureInfo.InvariantCulture) ?? "未知"}");
            if (!check.IsSufficient)
            {
                throw new UpdateFailedException(
                    UpdateStage.DiskCheck,
                    $"磁盘空间不足：需要 {check.RequiredBytes.ToString(CultureInfo.InvariantCulture)} 字节，可用 {check.Install.AvailableBytes?.ToString(CultureInfo.InvariantCulture) ?? "未知"}");
            }

            // 7 PermissionCheck
            stage = UpdateStage.PermissionCheck;
            InstallDirectoryProbeResult probe = _engine.ProbeInstallDirectory();
            if (!probe.IsWritable)
            {
                throw new UpdateFailedException(UpdateStage.PermissionCheck, probe.FailureReason ?? "安装目录不可写");
            }

            // 8 Commit
            stage = UpdateStage.Commit;
            var applyProgress = new ProgressRelay<UpdateProgressInfo>(info =>
                _events.RaiseProgress(new UpdateProgressEventArgs(info.Stage, info.Processed, info.Total, 0)));
            await Task.Run(() => _engine.Apply(downloaded, currentVersion, applyProgress, ct), ct).ConfigureAwait(false);

            state = _engine.LoadState();
            state.ChainLength++;
            TrySave(state);
            return new CycleOutcome(CycleOutcomeKind.Installed, targetVersion, Settings, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateFailedException ex)
        {
            failure = ex;
            original = ex;
        }
        catch (Exception ex)
        {
            failure = new UpdateFailedException(stage, ex.Message, ex);
            original = ex;
        }

        original = original.InnerException ?? original;

        // 失败流程
        Version from = current ?? ResolveCurrentVersion(state, warn: false);
        Log.Error(failure.Stage, failure.Message, original);
        await _events.RaiseFailedAsync(new UpdateFailedEventArgs(failure.Stage, failure, targetVersion)).ConfigureAwait(false);
        long durationMs = (long)_env.TimeProvider.GetElapsedTime(started).TotalMilliseconds;
        _engine.EnqueueReport(UpdateReportBuilder.Failed(
            state.DeviceGuid,
            _env,
            failure.Stage,
            failure.Message,
            from,
            targetVersion,
            durationMs,
            _engine.GetAvailableDiskBytes(),
            _options.Source.IncludeLogTailOnFailure ? _engine.ReadLogTail() : null,
            _env.TimeProvider.GetUtcNow()));

        return new CycleOutcome(chainLimit ? CycleOutcomeKind.ChainLimitReached : CycleOutcomeKind.Failed, targetVersion, Settings, failure);
    }

    /// <summary>按注入的 TimeProvider 精确等待（Task.Delay 会把到期时间截断到毫秒，抖动的到点时刻就不再可精确断言）。</summary>
    private async Task DelayExactAsync(TimeSpan delay, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ITimer timer = _env.TimeProvider.CreateTimer(_ => tcs.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);
        await using CancellationTokenRegistration registration = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task.ConfigureAwait(false);
    }

    private async Task<ValidatedFeed> FetchFeedAsync(UpdateState state, CancellationToken ct)
    {
        FeedResult result;
        try
        {
            string? sentETag = state.FeedETag;
            result = await _feed.GetAsync(sentETag, ct).ConfigureAwait(false);
            if (result.IsNotModified)
            {
                if (_cachedFeed is not null)
                {
                    Log.Information(UpdateStage.Check, "304 未变，沿用缓存的 feed 文档");
                    return _cachedFeed;
                }

                if (sentETag is null)
                {
                    throw new UpdateFailedException(UpdateStage.Check, "服务器对无条件请求返回 304");
                }

                result = await _feed.GetAsync(null, ct).ConfigureAwait(false);
                if (result.IsNotModified)
                {
                    throw new UpdateFailedException(UpdateStage.Check, "服务器对无条件请求返回 304");
                }
            }

            if (result.Document is null)
            {
                throw new UpdateFailedException(UpdateStage.Check, "feed 实现返回了空文档");
            }

            ValidatedFeed validated = ReleaseFeedValidator.Validate(result.Document, Log);
            _cachedFeed = validated;
            state.FeedETag = result.ETag;
            return validated;
        }
        catch (FeedRejectedException ex)
        {
            throw new UpdateFailedException(UpdateStage.Check, ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateFailedException(UpdateStage.Check, ex.Message, ex);
        }
    }

    private Version ResolveCurrentVersion(UpdateState state, bool warn)
    {
        if (_options.CurrentVersion is not null)
        {
            return _options.CurrentVersion;
        }

        if (state.CurrentVersion is not null)
        {
            return VersionNormalization.Canonical(state.CurrentVersion);
        }

        Version? installed = _engine.ReadInstalledVersion();
        if (installed is not null)
        {
            return VersionNormalization.Canonical(installed);
        }

        if (_env.EntryAssemblyVersion is not null)
        {
            return VersionNormalization.Canonical(_env.EntryAssemblyVersion);
        }

        if (warn)
        {
            Log.Warning(UpdateStage.Check, "无法确定当前版本，按 0.0.0.0 处理");
        }

        return VersionNormalization.Zero;
    }

    private void LogSelection(ReleaseFeedDocument document, Version current, SelectionResult selection)
    {
        Version? top = document.Releases.Count == 0 ? null : document.Releases.Max(r => r.Version);
        Log.Information(
            UpdateStage.Check,
            $"检查：当前 {current}，feed 最高 {top?.ToString() ?? "无"}，结论 {selection.Outcome}");

        foreach (SelectionTrace trace in selection.Traces)
        {
            string floor = trace.Verdict == SelectionVerdict.BelowFloor ? $"，minUpdatableFrom {trace.Floor}" : string.Empty;
            Log.Information(
                UpdateStage.Check,
                $"条目 {trace.Entry.Version}：{trace.Verdict}（灰度桶 {trace.Bucket.ToString(CultureInfo.InvariantCulture)}，rolloutPercent {trace.RolloutPercent.ToString(CultureInfo.InvariantCulture)}{floor}）");
        }
    }

    private void TrySave(UpdateState state)
    {
        try
        {
            _engine.SaveState(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(UpdateStage.Check, "无法保存状态文件，本轮继续", ex);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            _env.FileOperations.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(UpdateStage.Verify, $"无法删除未通过校验的升级包 {path}", ex);
        }
    }

    /// <summary>直接调用回调的进度桥接：System.Progress&lt;T&gt; 会捕获同步上下文并异步投递，这里不要。</summary>
    private sealed class ProgressRelay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
