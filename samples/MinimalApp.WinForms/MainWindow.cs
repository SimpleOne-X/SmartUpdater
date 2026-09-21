// 只对会与 System.Windows.Forms 撞名的 AntdUI 类型写别名；其余一律 AntdUI. 全限定，不引入 AntdUI 的整个命名空间
// （ImplicitUsings 会带上全局 using System.Windows.Forms，整体引入会引出 14 个 CS0104）。
// 注意 Modal 也要全限定：在窗体类里，简单名 Modal 会绑到继承来的 Form.Modal（bool 属性）。
using System.Globalization;
using AButton = AntdUI.Button;
using ALabel = AntdUI.Label;
using AMessage = AntdUI.Message;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>更新提示对话框里用户的选择。</summary>
internal enum UpdateChoice
{
    UpdateNow,
    Later,
    Skip,
}

/// <summary>
/// AntdUI.Window 派生窗体。AntdUI.Window 隐藏了系统标题栏，标题栏由 AntdUI.PageHeader 自绘
/// （ShowButton = true 才有最小化 / 关闭按钮）。全部用代码摆放控件，不用设计器。
/// 文字与控件状态由 <see cref="UpdateStatusModel"/> 决定，本类只负责把它摆到控件上。
/// </summary>
internal sealed class MainWindow : AntdUI.Window
{
    /// <summary>日志区超过这么多行就触发截断。</summary>
    private const int MaxLogLines = 500;

    /// <summary>触发截断时一次丢掉最旧的这么多行。</summary>
    private const int TrimLogLines = 100;

    private readonly AntdUI.PageHeader _header = new();
    private readonly AntdUI.Tag _versionTag = new();
    private readonly AntdUI.Tag _stateTag = new();
    private readonly ALabel _statusLabel = new();
    private readonly AntdUI.Progress _progress = new();
    private readonly AntdUI.Input _log = new();
    private readonly AButton _checkButton = new();
    private readonly ALabel _statusBar = new();

    // AntdUI.Input.AppendText 不受 MaxLength 限制，日志区会无限增长，
    // 所以自己按行截断：这里记着当前显示的每一行，超限时丢掉最旧的一批并整体重排。
    private readonly List<string> _logLines = [];

    private readonly StartupResult _startup;
    private readonly UpdateDriverOptions? _driverOptions;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _workCts = new();   // 只管工作循环：Restarting 里取消它，不能牵连 RunAsync 的令牌
    private readonly WorkLoop _workLoop = new();
    private UpdateDriver? _driver;
    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private volatile AntdUI.Modal.Config? _pendingModal;   // AskUpdate 里赋值，返回前清空；E2eHooks 在别的线程读

    private static System.Drawing.Icon? LoadWindowIcon()
    {
        using Stream? stream = typeof(MainWindow).Assembly.GetManifestResourceStream("X-logo.ico");
        return stream is null ? null : new System.Drawing.Icon(stream);
    }

    public MainWindow(StartupResult startup, SingleInstanceHandle instance, UpdateDriverOptions? driverOptions)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(instance);
        _startup = startup;
        _driverOptions = driverOptions;

        Icon = LoadWindowIcon();
        Text = "SmartUpdater 示例";                        // 任务栏 / Alt-Tab 显示的标题（窗口内可见的标题由 PageHeader 画）
        ClientSize = new Size(640, 440);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;

        // ---- 标题栏（自绘）----
        _header.Text = "SmartUpdater 示例";
        _header.SubText = "基于 AntdUI";
        _header.Dock = DockStyle.Top;
        _header.Height = 48;
        _header.ShowButton = true;                         // 默认 false：不设就没有最小化 / 关闭按钮
        _header.MaximizeBox = false;
        _header.DividerShow = true;

        // ---- 当前版本 / 状态标签 ----
        // 版本取 startup.CurrentVersion（四段，如 v1.0.0.0）。
        // 宽度给到 104：四段版本号如 v10.10.10.10 比三段长，72 会被裁。
        _versionTag.Text = "v" + startup.CurrentVersion;
        _versionTag.Type = AntdUI.TTypeMini.Info;
        _versionTag.Location = new Point(24, 64);
        _versionTag.Size = new Size(104, 26);

        _stateTag.Location = new Point(136, 64);
        _stateTag.Size = new Size(88, 26);

        _statusLabel.Location = new Point(24, 100);
        _statusLabel.Size = new Size(592, 28);
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // ---- 进度条（Value 取值 0..1）----
        _progress.Location = new Point(24, 134);
        _progress.Size = new Size(592, 24);
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // ---- 多行只读 Input 当日志区 ----
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.WordWrap = true;
        _log.AutoScroll = true;                            // 显示滚动条
        _log.PlaceholderText = "事件日志……";
        _log.Location = new Point(24, 170);
        _log.Size = new Size(592, 180);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        // ---- 唯一的按钮：重新发起一次检查（RunAsync 一开始就会立即检查一次）----
        _checkButton.Text = "检查更新";
        _checkButton.Type = AntdUI.TTypeMini.Primary;
        _checkButton.Location = new Point(24, 360);
        _checkButton.Size = new Size(132, 38);
        _checkButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _checkButton.Click += (_, _) => _ = CheckNowAsync();

        // ---- 状态栏：更新完成后在这里显示「已从 X 更新到 Y」，不弹窗----
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = 28;
        _statusBar.TextAlign = ContentAlignment.MiddleLeft;
        _statusBar.Padding = new Padding(24, 0, 24, 0);
        _statusBar.ForeColor = Color.Gray;

        Controls.AddRange(
        [
            _versionTag, _stateTag, _statusLabel, _progress, _log, _checkButton,
            _statusBar,
            _header,                                       // Dock=Top 的控件最后加入，才会占据最外层的顶边
        ]);

        ActiveControl = _checkButton;                      // 否则第一个可选中的控件（日志框）会带着焦点环启动

        SetStatus(UpdateStatusModel.Idle(startup.CurrentVersion));
        AppendEvent("窗口已创建。");

        // 另一个实例请求激活：把窗口带到前台是消费方自己的 UI 决定，包零 UI。
        // 事件在线程池线程上触发，必须回 UI 线程；用 BeginInvoke（异步）以免同步等待造成死锁。
        instance.ActivationRequested += (_, _) =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    Activate();
                    AppendEvent("另一个实例请求激活");
                });
            }
        };

        Shown += OnShown;
        FormClosed += OnFormClosed;
    }

    /// <summary>供 E2eHooks 等待弹窗出现：公开 API，比按内部类型名找窗口稳。</summary>
    internal static int OpenModalCount => AntdUI.Modal.ModalCount;

    private void OnShown(object? sender, EventArgs e)
    {
        // 更新后提示：只在状态栏说一句，不弹窗。
        if (_startup.JustUpdated && _startup.ToVersion is not null)
        {
            SetStatusBar(UpdateStatusModel.JustUpdatedStatusBar(_startup.FromVersion, _startup.ToVersion));
        }

        if (!_startup.IsUpdateEnabled)
        {
            // 更新被禁用时不构造 UpdateClient，程序照常可用。
            string text = "更新已禁用：" + _startup.UpdateDisabledReason;
            SetStatus(UpdateStatusModel.Idle(_startup.CurrentVersion) with
            {
                StatusText = text,
                TagText = "已禁用",
                CheckButtonEnabled = false,
            });
            SetStatusBar(text);
            AppendEvent(text);
            return;
        }

        if (_driverOptions is null)
        {
            SetStatusBar("未配置更新源：用 --feed-url <地址> 启动即可启用更新。");
            SetStatus(UpdateStatusModel.Idle(_startup.CurrentVersion) with { CheckButtonEnabled = false });
            return;
        }

        // 必须在 UI 线程构造：UpdateClient 在构造时捕获 SynchronizationContext，事件自动封送回这里，
        // 所以下面所有处理器都直接改控件，一行 Invoke 都不写。
        _driver = new UpdateDriver(_driverOptions, _startup.CurrentVersion);
        _driver.StatusChanged += (_, model) => SetStatus(model);
        _driver.EventLogged += (_, line) => AppendEvent(line);   // 任意线程；AppendEvent 内部 BeginInvoke
        _driver.ChoiceRequested += (_, offer) => offer.Choice = AskUpdate(offer.Version.ToString(), offer.Notes, offer.PackageSize);
        _driver.RestartRequested += (_, restart) =>
        {
            _workCts.Cancel();                             // 取消工作循环……
            restart.WaitFor(_workLoop.Task);               // ……并把它的收尾交给包等待
        };

        _workLoop.Start(_workCts.Token);
        _runTask = RunDriverAsync();
    }

    private async Task RunDriverAsync()
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            // 只有开了自动截图才会真的等：让"更新前"的那张图先拍完，再开始第一次检查。
            // 真实用户走的是 Task.CompletedTask，行为完全不变。
            await E2eHooks.WaitForFirstShotAsync();
            await _driver!.RunAsync(_runCts.Token);
            Application.Exit();                            // 正常返回 = 新进程已启动：旧进程必须退出
        }
        catch (OperationCanceledException)
        {
            // 关闭窗口或重新检查导致的取消。
        }
        catch (Exception ex)
        {
            AppendEvent("更新客户端异常：" + ex.Message);
        }
    }

    /// <summary>"检查更新"：取消当前这轮 RunAsync 再重新开始（开始时会立即检查一次）。</summary>
    private async Task CheckNowAsync()
    {
        if (_driver is null)
        {
            return;
        }

        AppendEvent("重新发起检查更新……");
        _runCts?.Cancel();
        await _runTask;                                    // RunAsync 不允许并发：等上一轮真正结束
        _runTask = RunDriverAsync();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _cts.Cancel();
        _workCts.Cancel();
        _runCts?.Cancel();
        _driver?.Dispose();
        _workLoop.Dispose();
    }

    /// <summary>把状态模型摆到控件上。必须在 UI 线程调用。</summary>
    internal void SetStatus(UpdateStatusModel model)
    {
        _statusLabel.Text = model.StatusText;
        _progress.Value = model.ProgressValue;
        _progress.State = model.TagKind switch
        {
            StatusKind.Good => AntdUI.TType.Success,
            StatusKind.Bad => AntdUI.TType.Error,
            _ => AntdUI.TType.None,
        };
        _stateTag.Text = model.TagText;
        _stateTag.Type = model.TagKind switch
        {
            StatusKind.Attention => AntdUI.TTypeMini.Warn,
            StatusKind.Working => AntdUI.TTypeMini.Info,
            StatusKind.Good => AntdUI.TTypeMini.Success,
            StatusKind.Bad => AntdUI.TTypeMini.Error,
            _ => AntdUI.TTypeMini.Default,
        };
        _checkButton.Enabled = model.CheckButtonEnabled;
    }

    /// <summary>底部状态栏文字。必须在 UI 线程调用。</summary>
    internal void SetStatusBar(string text) => _statusBar.Text = text;

    /// <summary>追加一条事件日志。可从任意线程调用：不在 UI 线程时用 BeginInvoke 投递。</summary>
    internal void AppendEvent(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            // 句柄未创建（窗口 Shown 之前）时从后台线程调用会静默丢弃这一行：
            // 所以所有后台回调（更新事件、进度）都只在 Shown 之后才开始接入。
            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke(() => AppendEvent(message));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // 窗口正在关闭：这时丢一行日志远好于让后台线程崩掉。
                }
            }

            return;
        }

        // 多行消息拆成多行，续行缩进到时间戳之后；每一行单独计数，这样 500 行的上限是"显示行"而不是"事件数"。
        string stamp = $"[{DateTime.Now:HH:mm:ss}] ";
        string indent = new(' ', stamp.Length);
        string[] newLines = message.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < newLines.Length; i++)
        {
            newLines[i] = (i == 0 ? stamp : indent) + newLines[i];
        }

        _logLines.AddRange(newLines);

        if (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveRange(0, TrimLogLines);
            _log.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
        }
        else
        {
            _log.AppendText(string.Join(Environment.NewLine, newLines) + Environment.NewLine);
        }

        _log.ScrollToEnd();
    }

    /// <summary>
    /// 三按钮更新对话框：现在更新 / 稍后 / 跳过。可在 UI 线程调用，也可在工作线程调用
    /// （库会封送到 UI 线程并阻塞调用线程）。内容里带更新说明与包体积。
    /// </summary>
    internal UpdateChoice AskUpdate(string version, string? notes, long packageSize)
    {
        var config = new AntdUI.Modal.Config(this, $"发现新版本 {version}", BuildPromptContent(notes, packageSize), AntdUI.TType.Info)
        {
            OkText = "现在更新",                            // 确定按钮 -> DialogResult.OK
            OkType = AntdUI.TTypeMini.Primary,
            CancelText = "稍后",                            // 取消按钮 / Esc / 关闭图标 -> DialogResult.No（不是 Cancel）
            Btns =
            [
                // 自定义按钮是"追加"在确定 / 取消之外的；它的 DialogResult 决定 Modal.open 的返回值。
                new AntdUI.Modal.Btn("skip", "跳过", AntdUI.TTypeMini.Default) { DialogResult = DialogResult.Ignore },
            ],
            // 需要用户做决定的弹窗必须关掉：默认 true，且窗体有"1 秒内 3 次激活变化就以 No 关闭"的启发式，
            // 弹出后 0.6 s 内点任何按钮都会拿到 No。
            MaskClosable = false,
        };

        _pendingModal = config;                            // 供 E2eHooks 应答；返回前清空
        DialogResult result;
        try
        {
            result = AntdUI.Modal.open(config);            // 阻塞（嵌套消息循环）直到弹窗关闭
        }
        finally
        {
            _pendingModal = null;
        }

        return result switch
        {
            DialogResult.OK => UpdateChoice.UpdateNow,
            DialogResult.Ignore => UpdateChoice.Skip,
            // No（稍后 / Esc / 关闭图标）、Cancel（被系统关闭）、None（库内部异常被吞掉时）：
            // 只有 OK 与 Ignore 触发动作，其余一律当"稍后"——这是安全的降级方向。
            _ => UpdateChoice.Later,
        };
    }

    /// <summary>供自动化应答：把正在显示的弹窗用给定结果关闭。可从任意线程调用；没有弹窗时什么也不做。</summary>
    internal void AnswerPendingModal(UpdateChoice choice)
    {
        AntdUI.Modal.Config? config = _pendingModal;
        if (config is null)
        {
            return;
        }

        config.DialogResult(choice switch
        {
            UpdateChoice.UpdateNow => DialogResult.OK,
            UpdateChoice.Skip => DialogResult.Ignore,
            _ => DialogResult.No,
        });
    }

    /// <summary>窗口内轻提示。不设 ShowInWindow 时，Message 默认弹在"屏幕"顶部居中，会盖在别的程序上。</summary>
    internal void Toast(AntdUI.TType icon, string text) =>
        AMessage.open(new AntdUI.Message.Config(this, text, icon) { ShowInWindow = true });

    private static string BuildPromptContent(string? notes, long packageSize)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            parts.Add("更新说明：" + notes.Trim());
        }

        if (packageSize > 0)
        {
            parts.Add("更新包大小：" + FormatSize(packageSize));
        }

        parts.Add("现在更新，还是稍后再说？");
        return string.Join(Environment.NewLine, parts);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
