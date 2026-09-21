using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// <c>pack</c> 命令：把已 publish 的应用目录打成升级包，落到 <c>&lt;output&gt;/packages/</c>，并新建或更新 <c>&lt;output&gt;/releases.json</c>。
/// <para>退出码：参数写错（缺选项、值非法、模式写错）走 <see cref="ExitCode.Usage"/>；输入内容有问题
/// （目录或文件缺失、包内路径不安全、已有 feed 不是合法 JSON）走 <see cref="ExitCode.Input"/>；
/// 与已有 feed 冲突走 <see cref="ExitCode.Conflict"/>；读写磁盘失败走 <see cref="ExitCode.Unexpected"/>。</para>
/// <para>落盘顺序：先在内存里把 zip 与新 feed 都算好，<b>全部成功后</b>才动磁盘——冲突、feed 损坏、路径不安全时
/// 一个字节都不落盘，也不留 <c>.tmp</c>。</para>
/// </summary>
internal static class PackCommand
{
    private const string FeedFileName = "releases.json";
    private const string PackagesDirectoryName = "packages";

    /// <summary>pack 认识的全部选项名（不带 <c>--</c>）。与 <see cref="HelpText.PackUsage"/> 的选项表由测试对账。</summary>
    internal static IReadOnlyList<string> KnownOptions { get; } =
    [
        "input",
        "version",
        "output",
        "package-name",
        "channel",
        "min-updatable-from",
        "mode",
        "rollout-percent",
        "notes",
        "notes-file",
        "preserve",
        "poll-interval-seconds",
        "jitter-window-seconds",
        "heartbeat-interval-seconds",
        "released-at",
        "force",
    ];

    /// <summary>zip 文件名前缀里不许出现的字符：路径分隔符与 Windows 非法字符（不随平台变，两个平台行为一致），
    /// 外加会破坏相对 URL 的 <c>#</c>、<c>%</c>（<c>?</c> 已在 Windows 非法字符里）。</summary>
    private const string InvalidPackageNameChars = "/\\:*?\"<>|#%";

    /// <summary>执行 pack，返回 <see cref="ExitCode"/> 里的值。输出写进 <paramref name="output"/> / <paramref name="error"/> 而不是直接用 Console，便于测试。</summary>
    public static int Run(ParsedCommandLine parsed, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // 只有"未知选项"这类用法错误才附用法表；值错误只打错误行（否则用法表里的全部选项名会让"错误里含选项名"的断言失去意义）。
        string? unknown = parsed.FindUnknownOption(KnownOptions);
        if (unknown is not null)
        {
            error.WriteLine($"未知选项 --{unknown}。");
            error.WriteLine(HelpText.PackUsage);
            return ExitCode.Usage;
        }

        PackOptions options;
        try
        {
            options = ReadOptions(parsed);
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Usage;
        }

        try
        {
            return Execute(options, output, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"读写磁盘失败：{ex.Message}");
            return ExitCode.Unexpected;
        }
    }

    private static int Execute(PackOptions options, TextWriter output, TextWriter error)
    {
        // 取绝对路径：命令行里正反斜杠混写的路径，打印出来会一半是 / 一半是 \，不便复制；
        // 同时把"根本不是合法路径"（含 < > | 等）当作参数错误尽早报出，而不是等到落盘时抛未处理异常。
        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(options.OutputDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error.WriteLine($"--output 不是合法的路径：{options.OutputDirectory}（{ex.Message}）");
            return ExitCode.Usage;
        }

        if (!Directory.Exists(options.InputDirectory))
        {
            error.WriteLine($"--input 指定的目录不存在：{options.InputDirectory}");
            return ExitCode.Input;
        }

        string? packageName = options.PackageName;
        string? derivationProblem = null;
        packageName ??= DeriveDefaultPackageName(options.InputDirectory, out derivationProblem);
        if (packageName is null)
        {
            error.WriteLine(derivationProblem);
            return ExitCode.Usage;
        }

        string? notes = options.Notes;
        if (options.NotesFile is not null)
        {
            if (!File.Exists(options.NotesFile))
            {
                error.WriteLine($"--notes-file 指定的文件不存在：{options.NotesFile}");
                return ExitCode.Input;
            }

            // ReadAllText 会识别并去掉 UTF-8 BOM：BOM 不属于说明文字，写进 feed 会成为一个看不见的 U+FEFF。
            notes = File.ReadAllText(options.NotesFile, Encoding.UTF8);
        }

        BuildResult build;
        try
        {
            build = PackageBuilder.Build(new BuildRequest(options.InputDirectory, options.Version, options.Preserve));
        }
        catch (DirectoryNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Input;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Input;
        }

        string packageFileName = $"{packageName}-{options.Version}.zip";
        var entry = new FeedEntryInput(
            options.Version,
            options.ReleasedAt,
            $"{PackagesDirectoryName}/{packageFileName}",
            build.ZipBytes.Length,
            build.Sha256Hex,
            options.MinUpdatableFrom,
            options.Mode,
            options.RolloutPercent,
            notes);
        var feedOptions = new FeedUpdateOptions(
            options.Channel,
            options.PollIntervalSeconds,
            options.JitterWindowSeconds,
            options.HeartbeatIntervalSeconds,
            options.Force);

        string feedPath = Path.Combine(outputDirectory, FeedFileName);
        string? existingFeed = File.Exists(feedPath) ? File.ReadAllText(feedPath, Encoding.UTF8) : null;

        // 先算出新 feed，成功了才写任何文件：冲突或旧 feed 损坏时，一个字节都不该落盘。
        string feedJson;
        try
        {
            feedJson = FeedWriter.Upsert(existingFeed, entry, feedOptions);
        }
        catch (JsonException ex)
        {
            error.WriteLine($"{feedPath} 无法作为 feed 读取，已保持原样、未做任何改动：{ex.Message}");
            return ExitCode.Input;
        }
        catch (FeedConflictException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Conflict;
        }

        string packagesDirectory = Path.Combine(outputDirectory, PackagesDirectoryName);
        string packagePath = Path.Combine(packagesDirectory, packageFileName);
        Directory.CreateDirectory(packagesDirectory);

        // 先 zip 后 feed：新条目对客户端可见的那一刻，它指向的包已经在那里了。
        // WriteAtomic 是"同目录 .tmp 再 Move 覆盖"，客户端可能正在轮询这两个文件。
        PackerJson.WriteAtomic(packagePath, build.ZipBytes);
        PackerJson.WriteAtomic(feedPath, Encoding.UTF8.GetBytes(feedJson));

        output.WriteLine($"已生成升级包：{packagePath}");
        output.WriteLine($"  版本：{options.Version}");
        output.WriteLine($"  大小：{build.ZipBytes.Length} 字节");
        output.WriteLine($"  sha256：{build.Sha256Hex}");
        output.WriteLine($"已更新 feed：{feedPath}");
        return ExitCode.Success;
    }

    /// <summary>
    /// 输入目录根部恰好一个 <c>*.exe</c> 时取它的基名。
    /// 自己按后缀过滤而不用 <c>*.exe</c> 通配：后者在 Linux 上区分大小写（<c>MyApp.EXE</c> 会漏），
    /// 在 Windows 上还有 8.3 短名带来的"多匹配"的隐患。
    /// </summary>
    private static string? DeriveDefaultPackageName(string inputDirectory, out string? problem)
    {
        string[] executables =
        [
            .. Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        ];

        if (executables.Length != 1)
        {
            problem = executables.Length == 0
                ? "输入目录根部没有 .exe，无法推导 zip 文件名前缀，请用 --package-name 指定。"
                : $"输入目录根部有 {executables.Length} 个 .exe，无法推导 zip 文件名前缀，请用 --package-name 指定。";
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(executables[0]);
        if (!IsValidPackageName(name, out string? nameProblem))
        {
            problem = $"由 {Path.GetFileName(executables[0])} 推导出的 zip 文件名前缀不能用（{nameProblem}），请用 --package-name 指定。";
            return null;
        }

        problem = null;
        return name;
    }

    private static bool IsValidPackageName(string name, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problem = "不能为空";
            return false;
        }

        foreach (char c in name)
        {
            if (InvalidPackageNameChars.Contains(c, StringComparison.Ordinal) || char.IsControl(c))
            {
                problem = $"不能含 {(char.IsControl(c) ? "控制字符" : c.ToString())}：它会成为 zip 文件名与 feed 里 package.url 的一部分";
                return false;
            }
        }

        problem = null;
        return true;
    }

    // ---- 选项读取：任何问题都抛 UsageException，Run 统一转成 ExitCode.Usage ----

    private static PackOptions ReadOptions(ParsedCommandLine parsed)
    {
        string input = Required(parsed, "input");
        Version version = ParseVersion("version", Required(parsed, "version"));
        string output = Required(parsed, "output");

        string? packageName = Optional(parsed, "package-name");
        if (packageName is not null && !IsValidPackageName(packageName, out string? nameProblem))
        {
            throw new UsageException($"--package-name 的值 \"{packageName}\" 不能用：{nameProblem}。");
        }

        string? channel = Optional(parsed, "channel");
        if (channel is not null && string.IsNullOrWhiteSpace(channel))
        {
            throw new UsageException("--channel 的值不能为空。");
        }

        string? minUpdatableFrom = Optional(parsed, "min-updatable-from");
        string mode = Optional(parsed, "mode") ?? "optional";
        string? rolloutPercent = Optional(parsed, "rollout-percent");
        string? notes = Optional(parsed, "notes");
        string? notesFile = Optional(parsed, "notes-file");
        if (notes is not null && notesFile is not null)
        {
            throw new UsageException("--notes 与 --notes-file 不能同时使用，请只给一个。");
        }

        return new PackOptions(
            input,
            version,
            output,
            packageName,
            channel,
            minUpdatableFrom is null ? null : ParseVersion("min-updatable-from", minUpdatableFrom),
            ParseMode(mode),
            rolloutPercent is null ? 100 : ParseRolloutPercent(rolloutPercent),
            notes,
            notesFile,
            ReadPreservePatterns(parsed),
            ReadSeconds(parsed, "poll-interval-seconds"),
            ReadSeconds(parsed, "jitter-window-seconds"),
            ReadSeconds(parsed, "heartbeat-interval-seconds"),
            ParseReleasedAt(Optional(parsed, "released-at")),
            ReadSwitch(parsed, "force"));
    }

    /// <summary>取只允许出现一次的选项值；没出现返回 null。重复出现或写成开关（没跟值）都是用法错误。</summary>
    private static string? Optional(ParsedCommandLine parsed, string name)
    {
        List<ParsedOption> occurrences = [.. parsed.Options.Where(o => string.Equals(o.Name, name, StringComparison.Ordinal))];
        return occurrences.Count switch
        {
            0 => null,
            1 => occurrences[0].Value ?? throw new UsageException($"--{name} 需要一个值。"),
            _ => throw new UsageException($"--{name} 只能出现一次。"),
        };
    }

    private static string Required(ParsedCommandLine parsed, string name)
    {
        string value = Optional(parsed, name) ?? throw new UsageException($"缺少必填选项 --{name}。");
        return string.IsNullOrWhiteSpace(value) ? throw new UsageException($"--{name} 的值不能为空。") : value;
    }

    /// <summary>
    /// 读开关。解析器的"--name value"规则会把 <c>--force x</c> 解析成 force="x"，此时 HasFlag 为 false、HasOption 为 true：
    /// 若不显式拒绝，开关会被静默当成"没给"，用户以为加了 --force 实际没生效。
    /// </summary>
    private static bool ReadSwitch(ParsedCommandLine parsed, string name)
    {
        bool present = false;
        foreach (ParsedOption option in parsed.Options.Where(o => string.Equals(o.Name, name, StringComparison.Ordinal)))
        {
            if (option.Value is not null)
            {
                throw new UsageException($"--{name} 是开关，不接受值（收到 \"{option.Value}\"）。");
            }

            present = true;
        }

        return present;
    }

    private static IReadOnlyList<string> ReadPreservePatterns(ParsedCommandLine parsed)
    {
        List<string> patterns = [];
        foreach (ParsedOption option in parsed.Options.Where(o => string.Equals(o.Name, "preserve", StringComparison.Ordinal)))
        {
            string pattern = option.Value ?? throw new UsageException("--preserve 需要一个值。");
            if (!GlobMatcher.IsValidPattern(pattern, out string? problem))
            {
                throw new UsageException($"--preserve 的模式无效：{problem}");
            }

            patterns.Add(pattern);
        }

        return patterns;
    }

    private static int? ReadSeconds(ParsedCommandLine parsed, string name)
    {
        string? text = Optional(parsed, name);
        if (text is null)
        {
            return null;
        }

        // NumberStyles.None：只认纯数字，不放行正负号与首尾空白。
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            ? seconds
            : throw new UsageException($"--{name} 必须是大于等于 0 的整数，收到 \"{text}\"。");
    }

    private static Version ParseVersion(string name, string text)
        => Version.TryParse(text, out Version? version)
            ? version
            : throw new UsageException($"--{name} 的值 \"{text}\" 不是合法的版本号（2 到 4 段数字，如 1.2.4）。");

    private static UpdateMode ParseMode(string text) => text switch
    {
        "optional" => UpdateMode.Optional,
        "mandatory" => UpdateMode.Mandatory,
        _ => throw new UsageException($"--mode 只接受 optional 或 mandatory，收到 \"{text}\"。"),
    };

    private static int ParseRolloutPercent(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent <= 100
            ? percent
            : throw new UsageException($"--rollout-percent 必须是 0 到 100 的整数，收到 \"{text}\"。");

    /// <summary>
    /// 缺省取当前 UTC 时间；结果一律截断到秒（feed 只写到秒）。
    /// 不带时区的输入按 UTC 解释（AssumeUniversal）：默认的 RoundtripKind 会按本机时区解释，
    /// 同一条命令在不同时区的机器上写出不同的 releasedAt，正违背"给定后输出可复现"。
    /// </summary>
    private static DateTimeOffset ParseReleasedAt(string? text)
    {
        DateTimeOffset value;
        if (text is null)
        {
            value = DateTimeOffset.UtcNow;
        }
        else if (!DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out value))
        {
            throw new UsageException($"--released-at 的值 \"{text}\" 不是合法的 ISO 8601 时间（如 2026-09-18T10:00:00Z）。");
        }

        return new DateTimeOffset(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
    }

    private sealed record PackOptions(
        string InputDirectory,
        Version Version,
        string OutputDirectory,
        string? PackageName,
        string? Channel,
        Version? MinUpdatableFrom,
        UpdateMode Mode,
        int RolloutPercent,
        string? Notes,
        string? NotesFile,
        IReadOnlyList<string> Preserve,
        int? PollIntervalSeconds,
        int? JitterWindowSeconds,
        int? HeartbeatIntervalSeconds,
        DateTimeOffset ReleasedAt,
        bool Force);

    private sealed class UsageException(string message) : Exception(message);
}
