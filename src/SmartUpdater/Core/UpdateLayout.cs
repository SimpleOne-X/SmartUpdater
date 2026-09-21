namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 客户端本地布局：所有路径都由注入的安装目录与 LocalAppData 目录计算，
/// 永不依赖当前工作目录。调用方传 <c>AppContext.BaseDirectory</c> 与 <c>%LOCALAPPDATA%</c>，测试传临时目录。
/// </summary>
internal sealed class UpdateLayout
{
    /// <summary>安装目录下存放包自身文件的子目录；整个目录不参与文件比对。</summary>
    public const string StateDirectoryName = ".smartupdater";

    public const string StateFileName = "state.json";
    public const string ManifestFileName = "manifest.json";
    public const string JournalFileName = "journal.json";
    public const string ReportsFileName = "reports.jsonl";
    public const string LogDirectoryName = "logs";

    /// <summary>下载缓存子目录名，位于 <c>%LOCALAPPDATA%\&lt;AppId&gt;\</c> 下。</summary>
    public const string DownloadCacheDirectoryName = "updates";

    /// <summary>包内清单在 zip 与安装目录里的相对路径（正斜杠）。</summary>
    public const string ManifestRelativePath = ".smartupdater/manifest.json";

    /// <param name="installDirectory">安装目录，通常传 <c>AppContext.BaseDirectory</c>。</param>
    /// <param name="localAppDataDirectory"><c>%LOCALAPPDATA%</c>，测试注入临时目录。</param>
    /// <param name="appId"><see cref="AppIdentity.Derive"/> 的产出或使用者覆盖值。</param>
    /// <exception cref="ArgumentException">任一参数为空白，或 <paramref name="appId"/> 不能安全地用作单个目录名。</exception>
    public UpdateLayout(string installDirectory, string localAppDataDirectory, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        // IsValid 只校验字符集；"." / ".."（后者会让 <localAppData>\<appId>\logs 逃出 LocalAppData）、
        // 结尾的点或空格、保留设备名都要在这里补上。AppIdentity.Derive 的产出恒带 "-<8 hex>" 后缀，天然通过。
        if (!AppIdentity.IsValid(appId)
            || appId.Trim('.').Length == 0
            || WindowsFileNames.EndsWithDotOrSpace(appId)
            || WindowsFileNames.IsReservedDeviceName(appId))
        {
            throw new ArgumentException($"AppId '{appId}' 不能安全地用作目录名。", nameof(appId));
        }

        AppId = appId;
        InstallDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        StateDirectory = Path.Combine(InstallDirectory, StateDirectoryName);
        StateFile = Path.Combine(StateDirectory, StateFileName);
        ManifestFile = Path.Combine(StateDirectory, ManifestFileName);
        JournalFile = Path.Combine(StateDirectory, JournalFileName);
        ReportsFile = Path.Combine(StateDirectory, ReportsFileName);
        LogDirectory = Path.Combine(StateDirectory, LogDirectoryName);

        AppDataDirectory = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppDataDirectory)), appId);
        DownloadCacheDirectory = Path.Combine(AppDataDirectory, DownloadCacheDirectoryName);
        FallbackLogDirectory = Path.Combine(AppDataDirectory, LogDirectoryName);
    }

    public string AppId { get; }

    /// <summary><see cref="Path.GetFullPath(string)"/> 之后去掉尾部分隔符。</summary>
    public string InstallDirectory { get; }

    /// <summary>&lt;install&gt;\.smartupdater</summary>
    public string StateDirectory { get; }

    /// <summary>&lt;install&gt;\.smartupdater\state.json</summary>
    public string StateFile { get; }

    /// <summary>&lt;install&gt;\.smartupdater\manifest.json</summary>
    public string ManifestFile { get; }

    /// <summary>&lt;install&gt;\.smartupdater\journal.json</summary>
    public string JournalFile { get; }

    /// <summary>&lt;install&gt;\.smartupdater\reports.jsonl</summary>
    public string ReportsFile { get; }

    /// <summary>&lt;install&gt;\.smartupdater\logs</summary>
    public string LogDirectory { get; }

    /// <summary>&lt;localAppData&gt;\&lt;appId&gt;</summary>
    public string AppDataDirectory { get; }

    /// <summary>&lt;localAppData&gt;\&lt;appId&gt;\updates</summary>
    public string DownloadCacheDirectory { get; }

    /// <summary>&lt;localAppData&gt;\&lt;appId&gt;\logs，安装目录下的日志目录不可写时用。</summary>
    public string FallbackLogDirectory { get; }

    /// <summary>
    /// 相对路径（正斜杠）→ 安装目录下的完整路径。
    /// 解析后逃出安装目录（含解析为安装目录本身）→ <see cref="ArgumentException"/>。
    /// 这是 zip-slip 的第二道防线：主防线是清单校验按路径段拒绝，这里对最终落盘路径再兜一次底。
    /// </summary>
    public string ResolveInstallPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string candidate = Path.GetFullPath(Path.Combine(InstallDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // 前缀必须带尾部分隔符：否则 "C:\app" 会把 "C:\app2\x" 当成自己的子路径。
        // 安装目录是盘符根（"C:\"）时 InstallDirectory 本身已带分隔符，不能再补一个。
        string prefix = Path.EndsInDirectorySeparator(InstallDirectory)
            ? InstallDirectory
            : InstallDirectory + Path.DirectorySeparatorChar;

        // 长度相等即解析结果就是安装目录本身（"sub/../"、"./" 会留下尾部分隔符），同样拒绝。
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || candidate.Length == prefix.Length)
        {
            throw new ArgumentException($"路径 '{relativePath}' 解析后逃出了安装目录。", nameof(relativePath));
        }

        return candidate;
    }

    /// <summary>&lt;DownloadCacheDirectory&gt;\&lt;version&gt;.zip。</summary>
    public string GetDownloadPath(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Path.Combine(DownloadCacheDirectory, $"{version}.zip");
    }
}
