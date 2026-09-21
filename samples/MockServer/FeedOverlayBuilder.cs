using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 算出要叠加到 <c>/releases.json</c> 上的字节。只做计算、不碰磁盘也不碰服务器状态，所以能被直接单测。
/// 失败一律返回 false 并说明原因 —— 写错版本号、字段名或底稿缺失却静默成功，是最难查的测试故障。
/// </summary>
internal static class FeedOverlayBuilder
{
    // 无 BOM：feed 的第一个字节必须是 '{'（或调用方给的任意字节），BOM 会让"逐字下发"名不副实。
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 与 Packer 的写出设置一致：端到端里 MockServer 吐出的 feed 和 Packer 生成的 feed 长得一样。
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 按请求算出要叠加的 feed 字节。<paramref name="diskFeedJson"/> 是磁盘上 releases.json 的原文，不存在传 null。
    /// 失败时 <paramref name="error"/> 说明原因，调用方据此返回 400。
    /// </summary>
    public static bool TryBuild(
        string? diskFeedJson,
        FeedOverlayRequest request,
        out byte[] bytes,
        [NotNullWhen(false)] out string? error)
    {
        bytes = [];

        bool changesEntries = request.RolloutPercent is not null
            || request.Mode is not null
            || request.PackageSha256 is not null;
        bool hasPatch = changesEntries
            || request.Version is not null
            || request.SchemaVersion is not null
            || request.Client is not null;

        if (request.Body is not null)
        {
            if (hasPatch)
            {
                error = "body 与 version / rolloutPercent / mode / packageSha256 / schemaVersion / client 互斥：要么逐字下发 body，要么改磁盘 feed 的字段。";
                return false;
            }

            bytes = Utf8NoBom.GetBytes(request.Body);
            error = null;
            return true;
        }

        if (request.SchemaVersion is null && request.Client is null && !changesEntries)
        {
            error = "没有要修改的字段：请给 body，或 rolloutPercent / mode / packageSha256 / schemaVersion / client 之一。";
            return false;
        }

        if (diskFeedJson is null)
        {
            error = "磁盘上没有 releases.json，没有底稿可改；要下发任意内容请用 body。";
            return false;
        }

        if (!TryParseObject(diskFeedJson, out JsonObject? root))
        {
            error = "磁盘上的 releases.json 不是合法的 JSON 对象，没有底稿可改；要下发任意内容请用 body。";
            return false;
        }

        if (request.SchemaVersion is { } schemaVersion)
        {
            root["schemaVersion"] = schemaVersion;
        }

        if (request.Client is { } clientPatch && !TryPatchClient(root, clientPatch, out error))
        {
            return false;
        }

        if ((request.Version is not null || changesEntries) && !TryPatchEntries(root, request, out error))
        {
            return false;
        }

        bytes = Utf8NoBom.GetBytes(root.ToJsonString(WriteOptions));
        error = null;
        return true;
    }

    private static bool TryParseObject(string json, [NotNullWhen(true)] out JsonObject? root)
    {
        try
        {
            // 重复的属性名必须在解析时就拒绝：JsonNode 默认惰性建字典，重复键要到第一次读写属性才抛 ArgumentException，
            // 那时已在 TryBuild 中途，会变成一个未处理异常（500）而不是一句 400 原因。
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        return root is not null;
    }

    private static bool TryPatchClient(JsonObject root, ClientPolicyPatch patch, [NotNullWhen(false)] out string? error)
    {
        JsonObject client;
        switch (root["client"])
        {
            case null:
                // 缺失与 JSON null 都当作"没有 client 段"，新建一个。
                client = new JsonObject();
                root["client"] = client;
                break;
            case JsonObject existing:
                client = existing;
                break;
            default:
                error = "磁盘 feed 里的 client 不是对象，无法改动。";
                return false;
        }

        if (patch.PollIntervalSeconds is { } poll)
        {
            client["pollIntervalSeconds"] = poll;
        }

        if (patch.JitterWindowSeconds is { } jitter)
        {
            client["jitterWindowSeconds"] = jitter;
        }

        if (patch.HeartbeatIntervalSeconds is { } heartbeat)
        {
            client["heartbeatIntervalSeconds"] = heartbeat;
        }

        error = null;
        return true;
    }

    private static bool TryPatchEntries(JsonObject root, FeedOverlayRequest request, [NotNullWhen(false)] out string? error)
    {
        if (root["releases"] is not JsonArray releases)
        {
            error = "磁盘 feed 里没有 releases 数组，无法选中条目。";
            return false;
        }

        List<JsonObject> targets = [];
        foreach (JsonNode? item in releases)
        {
            if (item is not JsonObject entry)
            {
                error = "磁盘 feed 的 releases 里有不是对象的条目，无法改动。";
                return false;
            }

            if (request.Version is null || IsVersion(entry, request.Version))
            {
                targets.Add(entry);
            }
        }

        if (targets.Count == 0)
        {
            error = request.Version is null
                ? "磁盘 feed 的 releases 为空，没有条目可改。"
                : $"磁盘 feed 里没有 version 为 \"{request.Version}\" 的条目（按字符串精确匹配）。";
            return false;
        }

        foreach (JsonObject entry in targets)
        {
            if (request.RolloutPercent is { } rolloutPercent)
            {
                entry["rolloutPercent"] = rolloutPercent;
            }

            if (request.Mode is { } mode)
            {
                entry["mode"] = mode;
            }

            if (request.PackageSha256 is { } sha256)
            {
                if (entry["package"] is not JsonObject package)
                {
                    error = "磁盘 feed 里被选中的条目没有 package 对象，无法改 packageSha256。";
                    return false;
                }

                package["sha256"] = sha256;
            }
        }

        error = null;
        return true;
    }

    private static bool IsVersion(JsonObject entry, string version)
        => entry["version"] is JsonValue value
            && value.TryGetValue(out string? text)
            && string.Equals(text, version, StringComparison.Ordinal);
}
