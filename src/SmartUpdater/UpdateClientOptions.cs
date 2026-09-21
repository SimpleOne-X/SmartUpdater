namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 更新客户端的全部输入。所有属性都是 <c>init</c>：构造后不可变；校验与钳制在创建 UpdateClient 时统一进行。
/// </summary>
public sealed class UpdateClientOptions
{
    /// <summary>feed（releases.json）的绝对 http / https 地址。与 <see cref="Feed"/> 恰好设置一个。</summary>
    public string? FeedUrl { get; init; }

    /// <summary>自带的 feed 实现。与 <see cref="FeedUrl"/> 恰好设置一个。</summary>
    public IReleaseFeed? Feed { get; init; }

    /// <summary>自带的包下载器。缺省为 null，表示按 feed 类型选择默认实现。</summary>
    public IPackageDownloader? Downloader { get; init; }

    /// <summary>更新报告的接收地址（绝对 http / https）。与 <see cref="Reporter"/> 至多设置一个；两者都为 null 表示不上报。</summary>
    public string? ReportUrl { get; init; }

    /// <summary>自带的报告上报实现。与 <see cref="ReportUrl"/> 至多设置一个。</summary>
    public IUpdateReporter? Reporter { get; init; }

    /// <summary>轮询间隔。缺省 300 秒（引用内部缺省值）；生效时钳制在 60 秒到 1 天之间，feed 下发的值优先。</summary>
    public TimeSpan PollInterval { get; init; } = ClientPolicyLimits.Defaults.PollInterval;

    /// <summary>抖动窗口。缺省 600 秒（引用内部缺省值）；生效时钳制在 0 到 1 天之间，feed 下发的值优先。</summary>
    public TimeSpan JitterWindow { get; init; } = ClientPolicyLimits.Defaults.JitterWindow;

    /// <summary>心跳上报间隔。缺省 21600 秒（引用内部缺省值）；生效时钳制在 300 秒到 7 天之间，feed 下发的值优先。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = ClientPolicyLimits.Defaults.HeartbeatInterval;

    /// <summary>重启前等待收尾任务的时间。缺省 15 秒；不得为负，超过 10 分钟按 10 分钟处理。</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>是否接受不受信任的 TLS 证书。缺省 false。</summary>
    public bool AllowUntrustedCertificates { get; init; }

    /// <summary>是否允许更新到低于当前版本的版本。缺省 false。</summary>
    public bool AllowVersionDowngrade { get; init; }

    /// <summary>
    /// 发布签名的验证公钥：SubjectPublicKeyInfo 的 base64 单行文本（P-256），即 <c>smartupdater sign</c> 输出的公钥。
    /// null 表示不验签；空串或空白视为配置错误。
    /// </summary>
    public string? PublicKey { get; init; }

    /// <summary>是否写文件日志。缺省 true。</summary>
    public bool EnableFileLogging { get; init; } = true;

    /// <summary>更新失败时是否在报告里附带日志尾部。缺省 true。</summary>
    public bool IncludeLogTailOnFailure { get; init; } = true;

    /// <summary>日志回调（级别、消息、异常）。缺省 null。</summary>
    public Action<UpdateLogLevel, string, Exception?>? LogCallback { get; init; }

    /// <summary>应用标识。缺省 null，表示由入口程序集名与主程序路径推导；显式设置时只允许 ASCII 字母数字与 - _ .，最长 64 个字符。</summary>
    public string? AppId { get; init; }

    /// <summary>主程序的完整路径。缺省 null，表示用当前进程路径（dotnet 宿主下改用入口程序集路径）；设置时必须是有根路径。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// 覆盖 <c>%LOCALAPPDATA%</c>（下载缓存与日志回退目录的根）。缺省 null，表示取真实的 Known Folder；设置时不能是空白，目录不存在不报错。
    /// 仅为可测试性而提供（<c>LOCALAPPDATA</c> 环境变量对 <c>Environment.GetFolderPath</c> 无效），生产代码不应设置。
    /// </summary>
    public string? LocalAppDataDirectory { get; init; }

    /// <summary>当前版本。缺省 null，表示依次取 state.json、已安装的 manifest、入口程序集版本。</summary>
    public Version? CurrentVersion { get; init; }
}
