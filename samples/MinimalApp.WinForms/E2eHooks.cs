using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>
/// 命令行选项的纯解析结果。<b>不传任何选项时，所有自动化钩子的字段都是 null / false</b>，
/// 示例的行为与真实用户看到的完全一样。纯逻辑，被单测覆盖。
/// </summary>
internal sealed record E2eHookOptions
{
    // 更新包自己的命令行开关（SmartUpdaterArgs）。它们是包的 internal 常量，示例作为普通消费方拿不到，
    // 所以在这里写死字面量：SmartUpdaterApp.Run 会消费它们，这里只负责认出来、不报 unknown-option。
    private const string PackageUpdated = "--smartupdater-updated";
    private const string PackageWaitPid = "--smartupdater-wait-pid";
    private const string PackageRollback = "--smartupdater-rollback";

    /// <summary>
    /// 更新后重启时，用来把钩子选项传给新进程的环境变量名。包把重启命令行固定成
    /// <c>--smartupdater-updated &lt;v&gt; --smartupdater-wait-pid &lt;pid&gt;</c>（不能为了 e2e 去改引擎），
    /// 所以钩子选项只能走环境块继承。真实用户不传任何钩子选项，这个变量就永远不会被写。
    /// </summary>
    public const string CarryOverVariable = "SMARTUPDATER_E2E_ARGS";

    /// <summary>feed 地址。不算"钩子"：没有它示例就没有更新源，真实用户也要配。</summary>
    public string? FeedUrl { get; init; }

    /// <summary>上报地址。</summary>
    public string? ReportUrl { get; init; }

    /// <summary>覆盖 LocalAppData 目录。</summary>
    public string? LocalAppDataDirectory { get; init; }

    /// <summary>验签公钥（单行 base64 SPKI）。</summary>
    public string? PublicKey { get; init; }

    /// <summary>覆盖轮询间隔。</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>覆盖抖动窗口。</summary>
    public TimeSpan? JitterWindow { get; init; }

    /// <summary>覆盖收尾等待上限。</summary>
    public TimeSpan? ShutdownTimeout { get; init; }

    /// <summary>允许版本降级。</summary>
    public bool AllowVersionDowngrade { get; init; }

    /// <summary>事件日志路径；非 null 即开启事件日志。</summary>
    public string? EventLogPath { get; init; }

    /// <summary>自动应答弹窗的表态；非 null 即开启自动应答。</summary>
    public UpdateChoice? AnswerModal { get; init; }

    /// <summary>自截图目录；非 null 即开启自截图。</summary>
    public string? ShotDirectory { get; init; }

    /// <summary>让 Restarting 的收尾任务永不返回。</summary>
    public bool HangShutdown { get; init; }

    /// <summary>兜底：这么久之后自行退出。</summary>
    public TimeSpan? ExitAfter { get; init; }

    /// <summary>不认识的选项（不致命）。</summary>
    public IReadOnlyList<string> UnknownOptions { get; init; } = [];

    /// <summary>缺值、值非法的选项名；对应的钩子不会开启。</summary>
    public IReadOnlyList<string> BadOptions { get; init; } = [];

    /// <summary>解析命令行。纯函数：不碰文件、进程、UI。</summary>
    public static E2eHookOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? feedUrl = null, reportUrl = null, localAppData = null, publicKey = null, eventLog = null, shotDir = null;
        TimeSpan? poll = null, jitter = null, shutdown = null, exitAfter = null;
        UpdateChoice? answer = null;
        bool allowDowngrade = false, hangShutdown = false;
        List<string> unknown = [];
        List<string> bad = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--allow-downgrade":
                    allowDowngrade = true;
                    break;
                case "--hang-shutdown":
                    hangShutdown = true;
                    break;
                case PackageRollback:
                    break;
                case PackageUpdated:
                case PackageWaitPid:
                    if (i + 1 < args.Length && !IsOption(args[i + 1]))
                    {
                        i++;
                    }

                    break;
                case "--feed-url":
                    feedUrl = TakeValue(args, ref i, bad) ?? feedUrl;
                    break;
                case "--report-url":
                    reportUrl = TakeValue(args, ref i, bad) ?? reportUrl;
                    break;
                case "--local-app-data":
                    localAppData = TakeValue(args, ref i, bad) ?? localAppData;
                    break;
                case "--public-key":
                    publicKey = TakeValue(args, ref i, bad) ?? publicKey;
                    break;
                case "--event-log":
                    eventLog = TakeValue(args, ref i, bad) ?? eventLog;
                    break;
                case "--shot-dir":
                    shotDir = TakeValue(args, ref i, bad) ?? shotDir;
                    break;
                case "--poll-seconds":
                    poll = TakeSeconds(args, ref i, bad) ?? poll;
                    break;
                case "--jitter-seconds":
                    jitter = TakeSeconds(args, ref i, bad) ?? jitter;
                    break;
                case "--shutdown-seconds":
                    shutdown = TakeSeconds(args, ref i, bad) ?? shutdown;
                    break;
                case "--exit-after-seconds":
                    exitAfter = TakeSeconds(args, ref i, bad) ?? exitAfter;
                    break;
                case "--answer-modal":
                    string? answerText = TakeValue(args, ref i, bad);
                    if (answerText is not null)
                    {
                        // 大小写敏感，且非法值不回落成任何一个分支：脚本打错字应当表现为"钩子没开"而不是"点了别的按钮"。
                        switch (answerText)
                        {
                            case "now":
                                answer = UpdateChoice.UpdateNow;
                                break;
                            case "later":
                                answer = UpdateChoice.Later;
                                break;
                            case "skip":
                                answer = UpdateChoice.Skip;
                                break;
                            default:
                                bad.Add(arg);
                                break;
                        }
                    }

                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new E2eHookOptions
        {
            FeedUrl = feedUrl,
            ReportUrl = reportUrl,
            LocalAppDataDirectory = localAppData,
            PublicKey = publicKey,
            PollInterval = poll,
            JitterWindow = jitter,
            ShutdownTimeout = shutdown,
            AllowVersionDowngrade = allowDowngrade,
            EventLogPath = eventLog,
            AnswerModal = answer,
            ShotDirectory = shotDir,
            HangShutdown = hangShutdown,
            ExitAfter = exitAfter,
            UnknownOptions = unknown,
            BadOptions = bad,
        };
    }

    /// <summary>
    /// 从命令行里挑出"本类认识的钩子选项"（连同它们的值），顺序不变。更新包自己的
    /// <c>--smartupdater-*</c>、不认识的选项、游离的值都会被丢掉。纯函数：不碰文件、进程、UI。
    /// </summary>
    public static string[] SelectHookArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> selected = [];
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (IsSwitchOption(arg))
            {
                selected.Add(arg);
                continue;
            }

            // 包自己的两个带值选项也要按"带值"跳过，否则它们的值会被当成游离 token 另做判断。
            bool isPackageValueOption = arg is PackageUpdated or PackageWaitPid;
            if (!IsValueOption(arg) && !isPackageValueOption)
            {
                continue;
            }

            bool hasValue = i + 1 < args.Length && !IsOption(args[i + 1]);
            if (!isPackageValueOption)
            {
                selected.Add(arg);
                if (hasValue)
                {
                    selected.Add(args[i + 1]);
                }
            }

            if (hasValue)
            {
                i++;
            }
        }

        return [.. selected];
    }

    /// <summary>
    /// 把钩子选项编码成 <see cref="CarryOverVariable"/> 的值（一行一个 token）。
    /// 一个钩子选项都没有时返回 null —— 真实用户不会写这个变量。纯函数。
    /// </summary>
    public static string? FormatCarryOver(string[] args)
    {
        string[] selected = SelectHookArguments(args);
        return selected.Length == 0 ? null : string.Join("\n", selected);
    }

    /// <summary>
    /// <see cref="FormatCarryOver"/> 的逆运算：把环境变量的值拆回参数数组。null / 空串得到空数组。纯函数。
    /// </summary>
    public static string[] ParseCarryOver(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// 解析前先补齐"重启后丢掉的钩子选项"：命令行上已经有钩子选项时原样返回（命令行优先），
    /// 否则把 <see cref="CarryOverVariable"/> 里继承来的选项接在命令行后面。
    /// 纯函数：只读环境变量（由调用方传入的委托提供），不碰文件、进程、UI，
    /// 因此仍满足"<c>SmartUpdaterApp.Run</c> 之前只允许纯参数解析"。
    /// </summary>
    public static string[] ResolveArguments(string[] args, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (SelectHookArguments(args).Length > 0)
        {
            return args;
        }

        string[] inherited = ParseCarryOver(getEnvironmentVariable(CarryOverVariable));
        return inherited.Length == 0 ? args : [.. args, .. inherited];
    }

    private static bool IsOption(string token) => token.StartsWith("--", StringComparison.Ordinal);

    // 下面两张表必须与 Parse 的 switch 保持一致，由 E2eHookOptionsTests 的往返用例钉住。
    private static bool IsSwitchOption(string token) => token is "--allow-downgrade" or "--hang-shutdown";

    private static bool IsValueOption(string token) => token is
        "--feed-url" or "--report-url" or "--local-app-data" or "--public-key" or
        "--event-log" or "--shot-dir" or "--answer-modal" or
        "--poll-seconds" or "--jitter-seconds" or "--shutdown-seconds" or "--exit-after-seconds";

    // 取选项的值（调用时 args[i] 是选项名）。缺值（没有下一个 token，或下一个以 -- 开头）时返回 null 并且不消费那个 token；
    // 值为空白同样算非法。失败一律把选项名记进 bad。
    private static string? TakeValue(string[] args, ref int i, List<string> bad)
    {
        string name = args[i];
        if (i + 1 >= args.Length || IsOption(args[i + 1]))
        {
            bad.Add(name);
            return null;
        }

        i++;
        if (string.IsNullOrWhiteSpace(args[i]))
        {
            bad.Add(name);
            return null;
        }

        return args[i];
    }

    private static TimeSpan? TakeSeconds(string[] args, ref int i, List<string> bad)
    {
        string name = args[i];
        string? text = TakeValue(args, ref i, bad);
        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        bad.Add(name);
        return null;
    }
}

/// <summary>
/// <b>全部</b>端到端自动化钩子都在这个文件里，且默认完全不生效：不传任何 <c>--…</c> 选项时
/// 不写事件日志、不截图、不自动应答、收尾正常返回。
/// </summary>
internal static class E2eHooks
{
    private static readonly Lock Gate = new();

    // 只在开了 --shot-dir 时才真的挡一下：见 WaitForFirstShotAsync。
    private static readonly TaskCompletionSource FirstShotGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static E2eHookOptions _options = E2eHookOptions.Parse([]);
    private static StreamWriter? _writer;
    private static StartupResult? _startup;
    private static System.Windows.Forms.Timer? _exitTimer;

    // progress 事件每个 stage 只写第一条与最后一条：记着当前 stage 与"尚未写出的最后一条"。
    private static string? _progressStage;
    private static (string Stage, string Percent)? _pendingProgress;

    /// <summary>Restarting 处理器读它：为 true 时收尾任务永不返回。</summary>
    public static bool IsShutdownHangEnabled => _options.HangShutdown;

    /// <summary>当前解析到的选项（未调用 <see cref="Initialize"/> 时全部是"关闭"）。</summary>
    public static E2eHookOptions Options => _options;

    /// <summary>
    /// 开了截图时，界面在第一次检查更新之前先等本进程这一张图拍完；没开截图返回已完成的任务，
    /// 真实用户一秒都不会等。第一张图要在 Shown 之后延迟 0.8 s 才能拍到画好的第一帧，而本地 feed
    /// 大约 0.3 s 就把弹窗弹出来了——不挡这一下，"更新前"那张图拍到的其实是弹窗（端到端脚本里有断言钉住）。
    /// 拍照无论成败都会放行（<see cref="ShootAfterRenderAsync"/> 的 finally），不会把更新挡死。
    /// </summary>
    public static Task WaitForFirstShotAsync()
        => _options.ShotDirectory is null ? Task.CompletedTask : FirstShotGate.Task;

    /// <summary>
    /// 按命令行装配钩子，并写第一行 <c>started</c>。事件日志打不开只留一条 Debug 输出，绝不让示例起不来。
    /// </summary>
    public static void Initialize(string[] args, StartupResult startup, int preheatedAssemblies)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(startup);

        _options = E2eHookOptions.Parse(args);
        _startup = startup;

        // 更新完成后，包会用固定的 --smartupdater-* 命令行重启本程序，钩子选项不会被带过去。
        // 放进本进程的环境块，重启出来的新进程就能继承到（ProcessLauncher 用 UseShellExecute=false
        // 且不改 StartInfo.Environment，子进程继承父进程环境块）。没有钩子选项时不写，真实用户不受影响。
        string? carryOver = E2eHookOptions.FormatCarryOver(args);
        if (carryOver is not null)
        {
            Environment.SetEnvironmentVariable(E2eHookOptions.CarryOverVariable, carryOver);
        }

        if (_options.EventLogPath is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(_options.EventLogPath));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var stream = new FileStream(_options.EventLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Debug.WriteLine("事件日志打不开：" + ex.Message);
            }
        }

        var started = new List<(string Key, string Value)>
        {
            ("version", startup.CurrentVersion.ToString()),
            ("pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
            ("app-id", startup.AppId),
            ("update-enabled", startup.IsUpdateEnabled.ToString()),
            ("preheated", preheatedAssemblies.ToString(CultureInfo.InvariantCulture)),
        };
        if (startup.JustUpdated && startup.FromVersion is not null)
        {
            started.Add(("updated-from", startup.FromVersion.ToString()));
        }

        Log("started", [.. started]);

        foreach (string name in _options.UnknownOptions)
        {
            Log("unknown-option", ("name", name));
        }

        foreach (string name in _options.BadOptions)
        {
            Log("bad-option", ("name", name));
        }

        Application.ApplicationExit += (_, _) => Log("exiting");
    }

    /// <summary>
    /// 把 <c>--feed-url</c> 等选项落成驱动器选项：没有 feed 就返回 null（真实用户没配更新源时的样子）；
    /// 时间参数只有显式传了才覆盖，否则用包的默认值。
    /// </summary>
    public static UpdateDriverOptions? CreateDriverOptions()
    {
        if (_options.FeedUrl is null)
        {
            return null;
        }

        UpdateDriverOptions defaults = UpdateDriverOptions.ForFeed(
            _options.FeedUrl, _options.ReportUrl, _options.LocalAppDataDirectory, _options.PublicKey);
        return defaults with
        {
            PollInterval = _options.PollInterval ?? defaults.PollInterval,
            JitterWindow = _options.JitterWindow ?? defaults.JitterWindow,
            ShutdownTimeout = _options.ShutdownTimeout ?? defaults.ShutdownTimeout,
            AllowVersionDowngrade = _options.AllowVersionDowngrade || defaults.AllowVersionDowngrade,
        };
    }

    /// <summary>按需挂上自动应答、自截图与兜底退出计时器。三个都没开时什么也不做。</summary>
    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_options.ShotDirectory is not null)
        {
            window.Shown += (_, _) => _ = ShootAfterRenderAsync(window, _startup is { JustUpdated: true } ? "03-after.png" : "01-before.png");
        }

        if (_options.AnswerModal is not null || _options.ShotDirectory is not null)
        {
            _ = Task.Run(() => HandleModalAsync(window, _options.AnswerModal));
        }

        if (_options.ExitAfter is { } after)
        {
            _exitTimer = new System.Windows.Forms.Timer { Interval = (int)Math.Clamp(after.TotalMilliseconds, 1, int.MaxValue) };
            _exitTimer.Tick += (_, _) =>
            {
                _exitTimer.Stop();
                Log("exit-after");
                Application.Exit();
            };
            _exitTimer.Start();
        }
    }

    /// <summary>写一行事件。没开事件日志时是空操作；线程安全；每行 Flush。</summary>
    public static void Log(string eventName, params (string Key, string Value)[] fields)
    {
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            FlushPendingProgress();
            WriteLine(eventName, fields);
        }
    }

    /// <summary>记一条进度：每个 stage 只写第一条与最后一条（percent 到 100 或 stage 切换时补写最后一条）。</summary>
    public static void LogProgress(string stage, double percent)
    {
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            string text = Math.Round(percent, 0).ToString("0", CultureInfo.InvariantCulture);
            if (!string.Equals(stage, _progressStage, StringComparison.Ordinal))
            {
                FlushPendingProgress();
                _progressStage = stage;
                WriteLine("progress", ("stage", stage), ("percent", text));
                return;
            }

            if (percent >= 100)
            {
                _pendingProgress = null;
                WriteLine("progress", ("stage", stage), ("percent", text));
                return;
            }

            _pendingProgress = (stage, text);
        }
    }

    /// <summary>同名文件已存在时依次加 <c>-2</c>、<c>-3</c>……（端到端脚本要连拍三次）。纯逻辑。</summary>
    internal static string NextFreePath(string directory, string fileName, Func<string, bool> exists)
    {
        string candidate = Path.Combine(directory, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 2; exists(candidate); n++)
        {
            candidate = Path.Combine(directory, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
        }

        return candidate;
    }

    // 调用方持有 Gate。
    private static void FlushPendingProgress()
    {
        if (_pendingProgress is { } p)
        {
            _pendingProgress = null;
            WriteLine("progress", ("stage", p.Stage), ("percent", p.Percent));
        }
    }

    // 调用方持有 Gate。
    private static void WriteLine(string eventName, params (string Key, string Value)[] fields)
    {
        if (_writer is null)
        {
            return;
        }

        var line = new StringBuilder();
        line.Append("ts=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        line.Append(" event=").Append(eventName);
        foreach ((string key, string value) in fields)
        {
            line.Append(' ').Append(key).Append('=').Append(Sanitize(value));
        }

        try
        {
            _writer.WriteLine(line.ToString());
        }
        catch (IOException ex)
        {
            Debug.WriteLine("事件日志写入失败：" + ex.Message);
        }
    }

    // 保持 key=value 可解析：值里的空白（含换行）一律换成下划线。
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        }

        return sb.ToString();
    }

    private static async Task ShootAfterRenderAsync(MainWindow window, string fileName)
    {
        try
        {
            // Shown 之后再等一小会儿，让 AntdUI 的自绘控件画完第一帧。
            await Task.Delay(800).ConfigureAwait(false);
            TakeShot(window, fileName);
        }
        finally
        {
            // 无论拍没拍成都要放行，否则 WaitForFirstShotAsync 会把更新检查永远挡住。
            FirstShotGate.TrySetResult();
        }
    }

    // 在后台任务里等弹窗，然后在 UI 线程上应答。绝不注入鼠标或按键（注入输入会被弹窗的防误触启发式吞成 No）。
    private static async Task HandleModalAsync(MainWindow window, UpdateChoice? choice)
    {
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (MainWindow.OpenModalCount > 0)
                {
                    // 弹出动画 + MaskClosable 启发式的安全边界（0.6 s 内应答，三个按钮全返回 No）。
                    await Task.Delay(700).ConfigureAwait(false);
                    TakeShot(window, "02-modal.png");          // 截图必须在应答之前
                    if (choice is { } c)
                    {
                        window.AnswerPendingModal(c);
                    }

                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            Log("answer-timeout");
        }
        catch (Exception ex)
        {
            // 钩子自己的失败只留痕，不能让后台任务的未观察异常影响示例。
            Log("failed", ("stage", "e2e-hook"), ("message", ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static void TakeShot(MainWindow window, string fileName)
    {
        string? dir = _options.ShotDirectory;
        if (dir is null)
        {
            return;
        }

        try
        {
            string path = NextFreePath(dir, fileName, File.Exists);
            bool ok = false;
            string? error = null;

            void Save() => ok = WindowShot.TrySave(path, out error);

            if (window.InvokeRequired)
            {
                window.Invoke(Save);
            }
            else
            {
                Save();
            }

            if (ok)
            {
                Log("shot", ("path", path));
            }
            else
            {
                Log("shot-failed", ("error", error ?? "unknown"));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException)
        {
            Log("shot-failed", ("error", ex.GetType().Name + ": " + ex.Message));
        }
    }
}
