using System.Security.Cryptography;

namespace SimpleOneX.SmartUpdater;

/// <summary>进程身份：AppId 哈希用哪个路径、重启时启动谁、主 exe 是哪一个。</summary>
/// <param name="ExecutablePath">主程序路径（dotnet 宿主下是入口程序集 dll 路径）。</param>
/// <param name="LaunchFileName">重启时启动的文件。</param>
/// <param name="LaunchArgumentPrefix">重启命令行里、应用自身参数之前的前缀（dotnet 宿主下是被引用的 dll 路径）。</param>
/// <param name="IsDotnetHost">是否经由 dotnet 宿主运行。</param>
internal sealed record ProcessIdentity(string ExecutablePath, string LaunchFileName, string LaunchArgumentPrefix, bool IsDotnetHost)
{
    /// <summary>主程序相对安装目录的路径（正斜杠），供 ApplyRequest.MainExecutableRelativePath 使用。</summary>
    /// <exception cref="InvalidOperationException">主程序不在安装目录下。</exception>
    public string RelativeExecutablePath(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        string relative = Path.GetRelativePath(Path.GetFullPath(installDirectory), Path.GetFullPath(ExecutablePath));
        bool escapes = relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

        if (escapes || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"主程序 {ExecutablePath} 不在安装目录 {installDirectory} 下");
        }

        return relative.Replace('\\', '/');
    }
}

/// <summary>AppId 与进程身份的解析。</summary>
internal static class AppIdResolver
{
    public const int MaxLength = 64;

    private const string UnknownExecutableMessage = "无法确定主程序路径，请设置 UpdateClientOptions.ExecutablePath";

    /// <summary>文件名（去扩展名）等于 "dotnet"，不分大小写。</summary>
    public static bool IsDotnetHost(string? processPath)
        => !string.IsNullOrEmpty(processPath)
           && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    public static ProcessIdentity ResolveProcessIdentity(UpdateEnvironment env, string? executablePathOverride)
    {
        ArgumentNullException.ThrowIfNull(env);

        if (executablePathOverride is not null)
        {
            return executablePathOverride.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IsDotnetHost(env.ProcessPath)
                ? new ProcessIdentity(executablePathOverride, env.ProcessPath!, CommandLine.Quote(executablePathOverride), true)
                : new ProcessIdentity(executablePathOverride, executablePathOverride, "", false);
        }

        if (IsDotnetHost(env.ProcessPath))
        {
            string? assemblyPath = env.CommandLineArguments.Length > 0 ? env.CommandLineArguments[0] : null;
            if (string.IsNullOrEmpty(assemblyPath) || !Path.IsPathRooted(assemblyPath))
            {
                throw new InvalidOperationException(UnknownExecutableMessage);
            }

            return new ProcessIdentity(assemblyPath, env.ProcessPath!, CommandLine.Quote(assemblyPath), true);
        }

        if (string.IsNullOrEmpty(env.ProcessPath))
        {
            throw new InvalidOperationException(UnknownExecutableMessage);
        }

        return new ProcessIdentity(env.ProcessPath, env.ProcessPath, "", false);
    }

    public static string ResolveAppId(UpdateEnvironment env, string? explicitAppId, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(identity);

        if (explicitAppId is not null)
        {
            Validate(explicitAppId);
            return explicitAppId;
        }

        return AppIdentity.Derive(env.EntryAssemblyName, identity.ExecutablePath);
    }

    public static void Validate(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);

        if (!AppIdentity.IsValid(appId))
        {
            throw new ArgumentException("AppId 只允许 ASCII 字母数字与 - _ .，且不能为空", nameof(appId));
        }

        if (appId.All(static c => c == '.'))
        {
            throw new ArgumentException("AppId 不能全为点号", nameof(appId));
        }

        if (WindowsFileNames.IsReservedDeviceName(appId))
        {
            throw new ArgumentException($"AppId {appId} 是 Windows 保留设备名", nameof(appId));
        }

        if (WindowsFileNames.EndsWithDotOrSpace(appId))
        {
            throw new ArgumentException("AppId 不能以点或空格结尾", nameof(appId));
        }

        if (appId.Length > MaxLength)
        {
            throw new ArgumentException($"AppId 超过 {MaxLength} 个字符", nameof(appId));
        }
    }
}

/// <summary>校验、钳制并解析后的客户端选项。全部校验集中在 <see cref="From"/>。</summary>
internal sealed class ResolvedClientOptions
{
    public static readonly TimeSpan MaxShutdownTimeout = TimeSpan.FromMinutes(10);

    private ResolvedClientOptions(
        UpdateClientOptions source,
        string appId,
        ProcessIdentity identity,
        ResolvedClientSettings localSettings,
        TimeSpan shutdownTimeout,
        string? publicKey,
        Version? currentVersion,
        Uri? feedUri,
        Uri? reportUri,
        string localAppDataDirectory)
    {
        Source = source;
        AppId = appId;
        Identity = identity;
        LocalSettings = localSettings;
        ShutdownTimeout = shutdownTimeout;
        PublicKey = publicKey;
        CurrentVersion = currentVersion;
        FeedUri = feedUri;
        ReportUri = reportUri;
        LocalAppDataDirectory = localAppDataDirectory;
    }

    public UpdateClientOptions Source { get; }

    public string AppId { get; }

    public ProcessIdentity Identity { get; }

    /// <summary>已钳下限与上限。</summary>
    public ResolvedClientSettings LocalSettings { get; }

    /// <summary>0 到 <see cref="MaxShutdownTimeout"/>。</summary>
    public TimeSpan ShutdownTimeout { get; }

    /// <summary>已用 ReleaseSignature.ImportPublicKey 验过；null 表示不验签。</summary>
    public string? PublicKey { get; }

    /// <summary>已规范化为四段。</summary>
    public Version? CurrentVersion { get; }

    public Uri? FeedUri { get; }

    public Uri? ReportUri { get; }

    /// <summary>已解析为绝对路径的本地数据根目录（下载缓存与日志回退目录的父目录）。</summary>
    public string LocalAppDataDirectory { get; }

    /// <summary>null 用 <paramref name="fallback"/>；空白抛 <see cref="ArgumentException"/>；否则 <see cref="Path.GetFullPath(string)"/>（目录不必存在）。</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> 是空白字符串。</exception>
    public static string ResolveLocalAppData(string? value, string fallback, string paramName)
    {
        if (value is null)
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} 不能是空白；使用真实目录请留 null", paramName);
        }

        return Path.GetFullPath(value);
    }

    /// <exception cref="ArgumentException">任一选项不合法；ParamName 为对应属性名。</exception>
    public static ResolvedClientOptions From(UpdateClientOptions options, UpdateEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(env);

        if ((options.Feed is null) == (options.FeedUrl is null))
        {
            throw new ArgumentException("Feed 与 FeedUrl 必须且只能设置一个", nameof(options.Feed));
        }

        Uri? feedUri = options.FeedUrl is null ? null : ParseHttpUri(options.FeedUrl, nameof(options.FeedUrl));

        if (options.Reporter is not null && options.ReportUrl is not null)
        {
            throw new ArgumentException("Reporter 与 ReportUrl 至多设置一个", nameof(options.Reporter));
        }

        Uri? reportUri = options.ReportUrl is null ? null : ParseHttpUri(options.ReportUrl, nameof(options.ReportUrl));

        RejectNegative(options.PollInterval, nameof(options.PollInterval));
        RejectNegative(options.JitterWindow, nameof(options.JitterWindow));
        RejectNegative(options.HeartbeatInterval, nameof(options.HeartbeatInterval));
        RejectNegative(options.ShutdownTimeout, nameof(options.ShutdownTimeout));

        string? publicKey = ValidatePublicKey(options.PublicKey);

        if (options.ExecutablePath is not null && !Path.IsPathRooted(options.ExecutablePath))
        {
            throw new ArgumentException("ExecutablePath 必须是有根路径（完整路径）", nameof(options.ExecutablePath));
        }

        ProcessIdentity identity = AppIdResolver.ResolveProcessIdentity(env, options.ExecutablePath);

        string appId;
        try
        {
            appId = AppIdResolver.ResolveAppId(env, options.AppId, identity);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message, nameof(options.AppId), ex);
        }

        ResolvedClientSettings local = ClientPolicyResolver.Resolve(
            null,
            new ResolvedClientSettings(options.PollInterval, options.JitterWindow, options.HeartbeatInterval));

        TimeSpan shutdown = options.ShutdownTimeout > MaxShutdownTimeout ? MaxShutdownTimeout : options.ShutdownTimeout;

        Version? currentVersion = options.CurrentVersion is null ? null : VersionNormalization.Canonical(options.CurrentVersion);

        string localAppData = ResolveLocalAppData(options.LocalAppDataDirectory, env.LocalApplicationDataDirectory, nameof(options.LocalAppDataDirectory));

        return new ResolvedClientOptions(options, appId, identity, local, shutdown, publicKey, currentVersion, feedUri, reportUri, localAppData);
    }

    private static Uri ParseHttpUri(string url, string paramName)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            return uri;
        }

        throw new ArgumentException($"{paramName} 必须是绝对的 http / https 地址：{url}", paramName);
    }

    private static void RejectNegative(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentException($"{paramName} 不能为负", paramName);
        }
    }

    private static string? ValidatePublicKey(string? publicKey)
    {
        if (publicKey is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("PublicKey 为空白；不启用验签请留 null", nameof(UpdateClientOptions.PublicKey));
        }

        try
        {
            using ECDsa key = ReleaseSignature.ImportPublicKey(publicKey);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new ArgumentException("PublicKey 不是合法的 P-256 SubjectPublicKeyInfo base64", nameof(UpdateClientOptions.PublicKey), ex);
        }

        return publicKey;
    }
}
