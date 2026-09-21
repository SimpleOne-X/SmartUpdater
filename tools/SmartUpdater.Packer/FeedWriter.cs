using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>要写进 feed 的一个发布条目。<see cref="PackageSha256"/> 与 <see cref="PackageSize"/> 取自实际写出的 zip。</summary>
internal sealed record FeedEntryInput(
    Version Version,
    DateTimeOffset ReleasedAt,
    string PackageUrl,
    long PackageSize,
    string PackageSha256,
    Version? MinUpdatableFrom,
    UpdateMode Mode,
    int RolloutPercent,
    string? Notes);

/// <summary>
/// 对已有 feed 的改动选项。<see cref="Channel"/> 为 null 表示"没指定"，按默认 stable 处理；
/// 三个 client 参数全为 null 时不碰 feed 里的 <c>client</c> 段。
/// </summary>
internal sealed record FeedUpdateOptions(
    string? Channel,
    int? PollIntervalSeconds,
    int? JitterWindowSeconds,
    int? HeartbeatIntervalSeconds,
    bool Force);

/// <summary>新条目与已有 feed 冲突（同版本内容不同、channel 不一致、同一版本混用三段与四段写法）。</summary>
internal sealed class FeedConflictException(string message) : Exception(message);

/// <summary>
/// 新建或更新 <c>releases.json</c>。已有 feed 走 <see cref="JsonNode"/> DOM 而不是 <see cref="ReleaseFeedDocument"/> 模型：
/// 模型往返会丢掉运维手加的未知字段，DOM 则原样保留。规则：
/// <list type="bullet">
/// <item><c>releases</c> 按版本（<see cref="Version"/> 比较，不是字符串比较）降序；解析不了版本的条目排到最后、保持相对顺序、不丢弃。</item>
/// <item>同版本：内容全等即幂等（保留原条目，连同它的 signature）；内容不同需要 Force，替换时旧 signature 随整条一起丢掉。
/// 比较字段是 url / size / sha256 / mode / rolloutPercent / minUpdatableFrom / notes，<b>忽略 releasedAt 与 signature</b>。</item>
/// <item>已有 channel 与本次不一致需要 Force。</item>
/// <item>client 段：三个参数任一非 null 才写 / 覆盖对应字段，全 null 则原样保留。</item>
/// <item>同一份 feed 里不能同时有 <c>1.2.4</c> 与 <c>1.2.4.0</c>（Version 不等、灰度桶不同），Force 也不放行。
/// 只比较新条目与已有条目，不检查已有条目彼此之间。</item>
/// </list>
/// </summary>
internal static class FeedWriter
{
    private const int FeedSchemaVersion = 1;
    private const string DefaultChannel = "stable";

    /// <exception cref="JsonException">已有 feed 不是合法 JSON，或结构不对（顶层不是对象、releases 不是数组）。</exception>
    /// <exception cref="FeedConflictException">与已有 feed 冲突。</exception>
    public static string Upsert(string? existingJson, FeedEntryInput entry, FeedUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        string channel = options.Channel ?? DefaultChannel;

        JsonObject root = existingJson is null ? NewFeed(channel) : ParseFeed(existingJson);
        JsonArray releases = GetOrAddReleases(root);

        // 全部冲突检查都在动 DOM 之前做完：DOM 是本次调用私有的，抛异常时不会有半成品外泄，
        // 但调用方（PackCommand）依赖"Upsert 抛了就一个字节都不写盘"，检查放前面更容易读懂。
        EnsureChannelMatches(root, channel, options.Force);
        EnsureNoMixedVersionForms(releases, entry.Version);

        List<JsonNode?> sameVersion = [.. releases.Where(node => TryGetVersion(node, out Version? v) && v == entry.Version)];
        bool identical = sameVersion.Count == 1 && SameContent(sameVersion[0], entry);
        if (!identical)
        {
            if (sameVersion.Count > 0 && !options.Force)
            {
                throw new FeedConflictException(
                    $"feed 里已有版本 {entry.Version} 的条目，且与本次打包的结果不一致（内容不同，或该版本重复出现）。"
                    + "如确要替换，请加 --force；旧条目的签名会随之丢弃，需要重新 sign。");
            }

            foreach (JsonNode? old in sameVersion)
            {
                releases.Remove(old);
            }

            releases.Add(BuildEntry(entry));
        }

        // 幂等时原条目原样保留（signature 也留着），但本次显式给的 client 参数照常生效。
        ApplyClient(root, options);
        root["channel"] = channel;
        SortDescending(releases);

        return root.ToJsonString(PackerJson.Write);
    }

    private static JsonObject NewFeed(string channel) => new()
    {
        ["schemaVersion"] = FeedSchemaVersion,
        ["channel"] = channel,
        ["releases"] = new JsonArray(),
    };

    private static JsonObject ParseFeed(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            // 重新包成基类 JsonException：System.Text.Json 抛出的是内部派生类型，调用方只该依赖公开的基类。
            throw new JsonException($"已有的 releases.json 不是合法的 JSON：{ex.Message}", ex);
        }

        return node as JsonObject
            ?? throw new JsonException("已有的 releases.json 顶层不是 JSON 对象。");
    }

    private static JsonArray GetOrAddReleases(JsonObject root)
    {
        JsonNode? node = root["releases"];
        if (node is null)
        {
            var created = new JsonArray();
            root["releases"] = created;
            return created;
        }

        return node as JsonArray
            ?? throw new JsonException("已有的 releases.json 里 releases 不是数组。");
    }

    /// <summary>已有 channel 缺失（或为 null）不算不一致；给了但取值不同（或根本不是字符串）需要 Force。</summary>
    private static void EnsureChannelMatches(JsonObject root, string channel, bool force)
    {
        JsonNode? existing = root["channel"];
        if (existing is null || force || IsString(existing, channel))
        {
            return;
        }

        throw new FeedConflictException(
            $"feed 里已有的 channel 是 {existing.ToJsonString()}，与本次的 \"{channel}\" 不一致（一个 feed 只服务一个 channel）。"
            + "请用 --channel 指定成与 feed 一致的值；确要改写 feed 的 channel 时加 --force。");
    }

    private static void EnsureNoMixedVersionForms(JsonArray releases, Version incoming)
    {
        foreach (JsonNode? node in releases)
        {
            if (TryGetVersion(node, out Version? existing)
                && existing != incoming
                && Normalize4(existing) == Normalize4(incoming))
            {
                throw new FeedConflictException(
                    $"feed 里已有版本 {existing}，本次要写入 {incoming}：两者补齐成四段后相同，却是客户端眼里两个不同的版本（灰度分桶也不同）。"
                    + "同一份 feed 里不允许混用三段与四段写法，请统一成同一种；--force 也不放行。");
            }
        }
    }

    /// <summary><c>1.2.4</c> 与 <c>1.2.4.0</c>、<c>1.2</c> 与 <c>1.2.0.0</c> 归一后相等。</summary>
    private static Version Normalize4(Version v)
        => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

    /// <summary>按 feed 文档示例的字段顺序建条目；没给的可选字段整个不写。</summary>
    private static JsonObject BuildEntry(FeedEntryInput e)
    {
        var entry = new JsonObject
        {
            ["version"] = e.Version.ToString(),
            ["releasedAt"] = e.ReleasedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["package"] = new JsonObject
            {
                ["url"] = e.PackageUrl,
                ["size"] = e.PackageSize,
                ["sha256"] = e.PackageSha256,
            },
        };

        if (e.MinUpdatableFrom is not null)
        {
            entry["minUpdatableFrom"] = e.MinUpdatableFrom.ToString();
        }

        entry["mode"] = ModeName(e.Mode);
        entry["rolloutPercent"] = e.RolloutPercent;

        if (e.Notes is not null)
        {
            entry["notes"] = e.Notes;
        }

        return entry;
    }

    private static string ModeName(UpdateMode mode) => mode switch
    {
        UpdateMode.Optional => "optional",
        UpdateMode.Mandatory => "mandatory",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的更新模式。"),
    };

    /// <summary>
    /// 只比类注释里"同版本"规则规定的字段。字段缺失（或为 null）就是"没给"，mode 与 rolloutPercent 缺失按客户端的缺省值
    /// （optional / 100）算：运维手写、没带这两个字段的条目，与我们显式写了缺省值的新条目语义相同，
    /// 判成冲突会逼人加 --force，而 --force 会丢掉它的签名。
    /// 字段给了但类型不对，与任何取值都不相等。
    /// </summary>
    private static bool SameContent(JsonNode? existing, FeedEntryInput entry)
    {
        if (existing is not JsonObject obj)
        {
            return false;
        }

        JsonNode? package = obj["package"];
        return FieldIs(package, "url", entry.PackageUrl)
            && FieldIs(package, "size", entry.PackageSize)
            && FieldIs(package, "sha256", entry.PackageSha256)
            && ModeIs(obj["mode"], entry.Mode)
            && RolloutIs(obj["rolloutPercent"], entry.RolloutPercent)
            && MinUpdatableFromIs(obj["minUpdatableFrom"], entry.MinUpdatableFrom)
            && FieldIs(obj, "notes", entry.Notes);
    }

    private static bool FieldIs(JsonNode? parent, string name, string? expected)
    {
        JsonNode? node = (parent as JsonObject)?[name];
        return node is null ? expected is null : expected is not null && IsString(node, expected);
    }

    private static bool FieldIs(JsonNode? parent, string name, long expected)
    {
        JsonNode? node = (parent as JsonObject)?[name];
        return node is JsonValue value && value.TryGetValue(out long actual) && actual == expected;
    }

    private static bool ModeIs(JsonNode? node, UpdateMode expected)
        => node is null
            ? expected == UpdateMode.Optional
            : node is JsonValue value
                && value.TryGetValue(out string? actual)
                && string.Equals(actual, ModeName(expected), StringComparison.OrdinalIgnoreCase);

    private static bool RolloutIs(JsonNode? node, int expected)
        => node is null
            ? expected == 100
            : node is JsonValue value && value.TryGetValue(out long actual) && actual == expected;

    private static bool MinUpdatableFromIs(JsonNode? node, Version? expected)
        => node is null
            ? expected is null
            : expected is not null
                && node is JsonValue value
                && value.TryGetValue(out string? text)
                && Version.TryParse(text, out Version? actual)
                && actual == expected;

    private static bool IsString(JsonNode node, string expected)
        => node is JsonValue value && value.TryGetValue(out string? actual) && actual == expected;

    private static bool TryGetVersion(JsonNode? node, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        return node is JsonObject obj
            && obj["version"] is JsonValue value
            && value.TryGetValue(out string? text)
            && Version.TryParse(text, out version);
    }

    private static void ApplyClient(JsonObject root, FeedUpdateOptions options)
    {
        if (options.PollIntervalSeconds is null
            && options.JitterWindowSeconds is null
            && options.HeartbeatIntervalSeconds is null)
        {
            return;
        }

        JsonObject client;
        switch (root["client"])
        {
            case JsonObject existing:
                client = existing;
                break;
            case null:
                // 新建的 client 段放在 releases 前面，与 feed 文档示例的顺序一致。
                client = new JsonObject();
                root.Remove("client");
                int at = root.IndexOf("releases");
                root.Insert(at < 0 ? root.Count : at, "client", client);
                break;
            default:
                throw new JsonException("已有的 releases.json 里 client 不是对象。");
        }

        if (options.PollIntervalSeconds is int poll)
        {
            client["pollIntervalSeconds"] = poll;
        }

        if (options.JitterWindowSeconds is int jitter)
        {
            client["jitterWindowSeconds"] = jitter;
        }

        if (options.HeartbeatIntervalSeconds is int heartbeat)
        {
            client["heartbeatIntervalSeconds"] = heartbeat;
        }
    }

    /// <summary>OrderBy 是稳定排序：解析不了版本的条目（key 相同）保持原有相对顺序。</summary>
    private static void SortDescending(JsonArray releases)
    {
        List<JsonNode?> sorted =
        [
            .. releases
                .Select(node => (Node: node, Version: TryGetVersion(node, out Version? v) ? v : null))
                .OrderBy(item => item.Version is null)
                .ThenByDescending(item => item.Version)
                .Select(item => item.Node),
        ];

        // 节点只能有一个父级：先清空（会解除父子关系）再按新顺序放回去。
        releases.Clear();
        foreach (JsonNode? node in sorted)
        {
            releases.Add(node);
        }
    }
}
