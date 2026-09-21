using System.Globalization;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>构造 <see cref="UpdateDriver"/> 所需的选项。</summary>
internal sealed record UpdateDriverOptions(
    string FeedUrl,
    string? ReportUrl,
    string? LocalAppDataDirectory,
    string? PublicKey,
    TimeSpan PollInterval,
    TimeSpan JitterWindow,
    TimeSpan ShutdownTimeout,
    bool AllowVersionDowngrade)
{
    /// <summary>只给 feed 地址，其余全部取包的默认值——真实用户看到的就是这套默认行为。</summary>
    public static UpdateDriverOptions ForFeed(string feedUrl, string? reportUrl = null, string? localAppDataDirectory = null, string? publicKey = null)
    {
        var defaults = new UpdateClientOptions();
        return new UpdateDriverOptions(
            feedUrl, reportUrl, localAppDataDirectory, publicKey,
            defaults.PollInterval, defaults.JitterWindow, defaults.ShutdownTimeout, defaults.AllowVersionDowngrade);
    }
}

/// <summary>optional 模式下交给界面的一次询问。</summary>
internal sealed class UpdateOfferEventArgs : EventArgs
{
    /// <summary>可安装的版本。</summary>
    public required Version Version { get; init; }

    /// <summary>当前版本。</summary>
    public required Version CurrentVersion { get; init; }

    /// <summary>更新说明。</summary>
    public string? Notes { get; init; }

    /// <summary>升级包大小（字节）。</summary>
    public long PackageSize { get; init; }

    /// <summary>界面填这个；<see cref="UpdateDriver"/> 据此调用 Accept / Postpone / Skip。默认"稍后"。</summary>
    public UpdateChoice Choice { get; set; } = UpdateChoice.Later;
}

/// <summary>包即将交接给新进程：界面在这里取消自己的工作循环，并把收尾任务交回。</summary>
internal sealed class RestartRequestedEventArgs : EventArgs
{
    private readonly RestartingEventArgs _inner;

    public RestartRequestedEventArgs(RestartingEventArgs inner) => _inner = inner;

    /// <summary>即将启动的新版本。</summary>
    public Version Version => _inner.Version;

    /// <summary>把收尾任务交给包统一等待。必须在处理器返回之前调用。</summary>
    public void WaitFor(Task task) => _inner.WaitFor(task);
}

/// <summary>
/// <b>唯一</b>引用 SmartUpdater 公开 API 的文件。界面、纯逻辑与自动化钩子都只认这里抛出的内部事件，
/// 因此包的 API 若有变动，只需要改这一个文件。
/// </summary>
internal sealed class UpdateDriver : IDisposable
{
    private readonly UpdateClient _client;
    private readonly Version _currentVersion;
    private readonly SynchronizationContext? _uiContext;
    private bool _offered;
    private bool _mandatoryShown;

    /// <summary>
    /// 必须在 UI 线程上构造：UpdateClient 在构造时捕获 SynchronizationContext.Current，
    /// 事件的自动封送全靠它。
    /// </summary>
    public UpdateDriver(UpdateDriverOptions options, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _currentVersion = currentVersion;
        _uiContext = SynchronizationContext.Current;

        _client = new UpdateClient(new UpdateClientOptions
        {
            FeedUrl = options.FeedUrl,
            ReportUrl = options.ReportUrl,
            LocalAppDataDirectory = options.LocalAppDataDirectory,
            PublicKey = options.PublicKey,
            PollInterval = options.PollInterval,
            JitterWindow = options.JitterWindow,
            ShutdownTimeout = options.ShutdownTimeout,
            AllowVersionDowngrade = options.AllowVersionDowngrade,
            CurrentVersion = currentVersion,
            EnableFileLogging = true,
            LogCallback = OnLog,
        });

        _client.UpdateAvailable += OnUpdateAvailable;
        _client.ProgressChanged += OnProgressChanged;
        _client.Restarting += OnRestarting;
        _client.Failed += OnFailed;
    }

    /// <summary>界面状态变化。</summary>
    public event EventHandler<UpdateStatusModel>? StatusChanged;

    /// <summary>一行 key=value 形式的事件日志（含包自己的日志回调）。可能在任意线程触发。</summary>
    public event EventHandler<string>? EventLogged;

    /// <summary>optional 模式的询问；处理器同步返回（界面在这里弹窗并阻塞）。</summary>
    public event EventHandler<UpdateOfferEventArgs>? ChoiceRequested;

    /// <summary>即将重启：界面取消工作循环并 WaitFor 它的任务。</summary>
    public event EventHandler<RestartRequestedEventArgs>? RestartRequested;

    /// <summary>正常返回 = 新进程已启动，消费方应当退出（RunAsync 的契约）。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 公开事件里没有"开始检查"这一项：RunAsync 一开始就立即检查一次，所以在这里留痕。
        Log("checking");
        await _client.RunAsync(cancellationToken).ConfigureAwait(true);
        Log("restart-ready");
    }

    /// <summary>释放客户端；调用前先取消正在运行的 <see cref="RunAsync"/>。</summary>
    public void Dispose() => _client.Dispose();

    private void OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        CheckThread(nameof(UpdateClient.UpdateAvailable));

        // 双保险：mandatory 不会走到这里。
        if (e.Mode == UpdateMode.Mandatory)
        {
            return;
        }

        _offered = true;
        Log("offered", ("version", e.Version.ToString()), ("mode", e.Mode.ToString()));
        StatusChanged?.Invoke(this, UpdateStatusModel.Available(_currentVersion, e.Version));

        var offer = new UpdateOfferEventArgs
        {
            Version = e.Version,
            CurrentVersion = e.CurrentVersion,
            Notes = e.Notes,
            PackageSize = e.PackageSize,
        };
        ChoiceRequested?.Invoke(this, offer);

        string choice;
        switch (offer.Choice)
        {
            case UpdateChoice.UpdateNow:
                e.Accept();
                choice = "now";
                break;
            case UpdateChoice.Skip:
                e.Skip();
                StatusChanged?.Invoke(this, UpdateStatusModel.Skipped(_currentVersion, e.Version));
                choice = "skip";
                break;
            default:
                e.Postpone();
                StatusChanged?.Invoke(this, UpdateStatusModel.Postponed(_currentVersion, e.Version));
                choice = "later";
                break;
        }

        Log("answered", ("choice", choice), ("version", e.Version.ToString()));
    }

    private void OnProgressChanged(object? sender, UpdateProgressEventArgs e)
    {
        CheckThread(nameof(UpdateClient.ProgressChanged));

        UpdatePhase? phase = e.Stage switch
        {
            UpdateStage.Download => UpdatePhase.Download,
            UpdateStage.Verify or UpdateStage.DiskCheck or UpdateStage.PermissionCheck => UpdatePhase.Verify,
            UpdateStage.Commit or UpdateStage.Rollback => UpdatePhase.Commit,
            _ => null,                                     // Check：不改进度
        };
        if (phase is null)
        {
            return;
        }

        // 没弹过窗就出现了下载 = mandatory：先把界面切到"强制更新"。
        if (phase == UpdatePhase.Download && !_offered && !_mandatoryShown)
        {
            _mandatoryShown = true;
            StatusChanged?.Invoke(this, UpdateStatusModel.Mandatory(_currentVersion));
        }

        E2eHooks.LogProgress(e.Stage.ToString(), e.Percent);
        StatusChanged?.Invoke(this, UpdateStatusModel.Progress(phase.Value, _currentVersion, e.Percent / 100.0));
    }

    private void OnRestarting(object? sender, RestartingEventArgs e)
    {
        CheckThread(nameof(UpdateClient.Restarting));

        RestartRequested?.Invoke(this, new RestartRequestedEventArgs(e));

        // --hang-shutdown 让收尾任务永不返回，包必须在 ShutdownTimeout 后照样继续交接。
        // 默认（不传该选项）时这里什么也不做。
        if (E2eHooks.IsShutdownHangEnabled)
        {
            e.WaitFor(new TaskCompletionSource().Task);
        }

        Log("restarting", ("version", e.Version.ToString()));
    }

    private void OnFailed(object? sender, UpdateFailedEventArgs e)
    {
        CheckThread(nameof(UpdateClient.Failed));

        StatusChanged?.Invoke(this, UpdateStatusModel.Failed(e.Stage.ToString(), e.Exception.Message));
        Log("failed", ("stage", e.Stage.ToString()), ("message", e.Exception.Message));
    }

    /// <summary>"事件已自动封送回 UI 线程"的运行期自证：违规就留痕并抛出。</summary>
    private void CheckThread(string handler)
    {
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            Log("cross-thread-violation", ("handler", handler));
            throw new InvalidOperationException($"事件处理器 {handler} 没有在 UI 线程上被调用。");
        }
    }

    private void OnLog(UpdateLogLevel level, string message, Exception? exception)
    {
        string line = $"[{level}] {message}";
        if (exception is not null)
        {
            line += " (" + exception.GetType().Name + ": " + exception.Message + ")";
        }

        EventLogged?.Invoke(this, line);
    }

    private void Log(string name, params (string Key, string Value)[] fields)
    {
        var parts = new List<string>(fields.Length + 1) { "event=" + name };
        foreach ((string key, string value) in fields)
        {
            // 保持 key=value 可解析：值里的空白一律换成下划线。
            string clean = string.Join('_', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={clean}"));
        }

        EventLogged?.Invoke(this, string.Join(' ', parts));
        E2eHooks.Log(name, fields);                        // 自动化事件日志（没开时是空操作）
    }
}
