namespace SimpleOneX.SmartUpdater;

/// <summary>整份 feed 不可用（不支持的 schemaVersion、重复版本）。调用方以 <see cref="UpdateStage.Check"/> 上报。</summary>
internal sealed class FeedRejectedException : Exception
{
    public FeedRejectedException(string message)
        : base(message)
    {
    }
}

/// <summary>校验后的 feed：<see cref="Document"/> 供挑选与展示（版本四段、降序、已过滤），<see cref="RawOf"/> 供验签（原始条目）。</summary>
internal sealed class ValidatedFeed
{
    private readonly Dictionary<ReleaseEntry, ReleaseEntry> _rawByNormalized;

    internal ValidatedFeed(ReleaseFeedDocument document, Dictionary<ReleaseEntry, ReleaseEntry> rawByNormalized)
    {
        Document = document;
        _rawByNormalized = rawByNormalized;
    }

    /// <summary>规范化后的文档。</summary>
    public ReleaseFeedDocument Document { get; }

    /// <summary>规范化条目对应的、解析自 feed 的原始条目（签名覆盖的是它）。</summary>
    public ReleaseEntry RawOf(ReleaseEntry normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return _rawByNormalized.TryGetValue(normalized, out ReleaseEntry? raw)
            ? raw
            : throw new ArgumentException("不是本 feed 的条目。", nameof(normalized));
    }
}

/// <summary>feed 的语义校验与归一：文档级缺陷拒绝整份文档，条目级缺陷跳过条目。</summary>
internal static class ReleaseFeedValidator
{
    /// <summary>本客户端认识的协议大版本。</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>SHA-256 十六进制串的长度。</summary>
    public const int Sha256HexLength = 64;

    /// <summary>校验并返回新文档：条目按版本降序，版本四段规范化，ParseWarnings 已记日志并清空；原始条目可经 RawOf 取回。</summary>
    public static ValidatedFeed Validate(ReleaseFeedDocument document, IUpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        foreach (string warning in document.ParseWarnings)
        {
            log.Warning(UpdateStage.Check, $"feed 条目被跳过：{warning}");
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new FeedRejectedException(
                $"不支持的 feed schemaVersion {document.SchemaVersion}（缺失按 0 计），本客户端仅支持 {SupportedSchemaVersion}。");
        }

        var kept = new List<ReleaseEntry>(document.Releases.Count);
        var rawByNormalized = new Dictionary<ReleaseEntry, ReleaseEntry>(ReferenceEqualityComparer.Instance);

        foreach (ReleaseEntry entry in document.Releases)
        {
            // 自带 IReleaseFeed 绕过 Parse，Version 可能为 null；必须先于 Canonical 检查
            if (entry is null || entry.Version is null)
            {
                log.Warning(UpdateStage.Check, "feed 条目被跳过：version 缺失或为 null。");
                continue;
            }

            Version version = VersionNormalization.Canonical(entry.Version);

            string? defect = Inspect(entry);
            if (defect is not null)
            {
                log.Warning(UpdateStage.Check, $"feed 条目 {version} 被跳过：{defect}");
                continue;
            }

            int rollout = entry.RolloutPercent;
            if (rollout is < 0 or > 100)
            {
                rollout = Math.Clamp(rollout, 0, 100);
                log.Warning(UpdateStage.Check, $"feed 条目 {version} 的 rolloutPercent {entry.RolloutPercent} 超出 0~100，按 {rollout} 处理。");
            }

            Version? floor = entry.MinUpdatableFrom is null ? null : VersionNormalization.Canonical(entry.MinUpdatableFrom);

            bool unchanged = ReferenceEquals(version, entry.Version)
                && ReferenceEquals(floor, entry.MinUpdatableFrom)
                && rollout == entry.RolloutPercent;

            ReleaseEntry normalized = unchanged
                ? entry
                : new ReleaseEntry
                {
                    Version = version,
                    ReleasedAt = entry.ReleasedAt,
                    Package = entry.Package,
                    MinUpdatableFrom = floor,
                    Mode = entry.Mode,
                    RolloutPercent = rollout,
                    Notes = entry.Notes,
                    Signature = entry.Signature,
                };

            kept.Add(normalized);
            rawByNormalized[normalized] = entry;
        }

        var duplicates = kept
            .GroupBy(r => r.Version)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();

        if (duplicates.Count > 0)
        {
            // 重复版本意味着发布管线坏了：挑哪条取决于数组顺序、包 URL 也随之变化，静默去重等于赌一个
            throw new FeedRejectedException($"feed 中以下版本出现多次：{string.Join(", ", duplicates)}。");
        }

        kept.Sort((a, b) => b.Version.CompareTo(a.Version));

        var normalizedDocument = new ReleaseFeedDocument
        {
            SchemaVersion = document.SchemaVersion,
            Channel = document.Channel,
            Client = document.Client,
            Releases = kept,
            ParseWarnings = [],
        };

        return new ValidatedFeed(normalizedDocument, rawByNormalized);
    }

    private static string? Inspect(ReleaseEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Package.Url))
        {
            return "package.url 为空。";
        }

        if (entry.Package.Sha256.Length != Sha256HexLength || !entry.Package.Sha256.All(Uri.IsHexDigit))
        {
            return "package.sha256 必须是 64 位十六进制。";
        }

        if (entry.Package.Size <= 0)
        {
            return $"package.size 必须大于 0（实际 {entry.Package.Size}）。";
        }

        if (entry.ReleasedAt == default)
        {
            return "releasedAt 缺失。";
        }

        return null;
    }
}
