using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 控制面：测试与端到端脚本靠它摆放故障、读回上报、一键复位。
/// 成功一律 204（读回上报是 200 + JSON 数组）；请求体不合法一律 400。
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(WebApplication app, ControlState state)
    {
        app.MapPost("/_control/faults", async (HttpContext context) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (!TryParseFault(body, out FaultRequest? fault))
            {
                return Results.BadRequest();
            }

            state.SetFault(fault);
            return Results.NoContent();
        });

        app.MapDelete("/_control/faults", () =>
        {
            state.ClearFaults();
            return Results.NoContent();
        });

        // feed 叠加：改的是内存里的 /releases.json，磁盘一字不改。走 IFileProvider 而不是自己写端点，
        // 这样 ETag / 304 / Range 仍由静态文件中间件原生处理。失败一律 400 + 一句原因（纯文本）。
        app.MapPost("/_control/feed-overlay", async (HttpContext context) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(
                    context.Request.Body,
                    documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false },
                    cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Reject("请求体不是合法的 JSON（重复的属性名也不接受）。");
            }

            if (!TryParseOverlay(body, out FeedOverlayRequest? overlay, out string? parseError))
            {
                return Reject(parseError);
            }

            string? diskFeed = await ReadDiskFeedAsync(state.Root, context.RequestAborted);
            if (!FeedOverlayBuilder.TryBuild(diskFeed, overlay, out byte[] bytes, out string? buildError))
            {
                return Reject(buildError);
            }

            // 每次叠加都推进 LastModified：ETag 由它与长度算出，不推进则同长度的新内容拿到旧 ETag，客户端永远 304。
            state.Files.Set(ControlState.FeedUrlPath, bytes, state.NextOverlayStamp());
            return Results.NoContent();
        });

        app.MapDelete("/_control/feed-overlay", () =>
        {
            state.ClearOverlay();
            return Results.NoContent();
        });

        // 限速与中途断流：改的是响应体怎么写出去（TransferMiddleware），静态文件本身不变。
        // 失败一律 400 + 一句原因（纯文本），重复的属性名同样是 400 而不是 500。
        app.MapPost("/_control/transfer", async (HttpContext context) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(
                    context.Request.Body,
                    documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false },
                    cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Reject("请求体不是合法的 JSON（重复的属性名也不接受）。");
            }

            if (!TryParseTransfer(body, out TransferRequest? transfer, out string? error))
            {
                return Reject(error);
            }

            state.SetTransfer(transfer);
            return Results.NoContent();
        });

        app.MapDelete("/_control/transfer", () =>
        {
            state.ClearTransfers();
            return Results.NoContent();
        });

        app.MapGet("/_control/reports", () => Results.Json(state.SnapshotReports()));

        app.MapDelete("/_control/reports", () =>
        {
            state.ClearReports();
            return Results.NoContent();
        });

        // 一次清空全部可变状态。四块状态各持各的锁，所以 reset 不是原子的：与它并发的控制面写入
        // （另一个测试同时在摆故障 / 叠加 / 传输设置）可能落在两次清除之间而"活过"这次 reset。
        // 测试与端到端脚本都是顺序调用控制面，不会触发；并发使用同一台服务器的调用方要自己排好顺序。
        app.MapPost("/_control/reset", () =>
        {
            state.ClearFaults();
            state.ClearReports();
            state.ClearOverlay();
            state.ClearTransfers();
            return Results.NoContent();
        });
    }

    /// <summary>
    /// 手工从 <see cref="JsonNode"/> 取值，而不是反序列化成 <see cref="FaultRequest"/>：
    /// 反序列化会把缺失的 <c>path</c> 悄悄变成 null，绕过"必填"这条规则。
    /// 这里"字段缺失"与"字段为 JSON null"一视同仁（<see cref="JsonObject"/> 的索引器两种情况都返回 null）。
    /// </summary>
    private static bool TryParseFault(JsonNode? body, [NotNullWhen(true)] out FaultRequest? fault)
    {
        fault = null;

        if (body is not JsonObject obj
            || obj["path"] is not JsonValue pathValue
            || !pathValue.TryGetValue(out string? path)
            || obj["status"] is not JsonValue statusValue
            || !statusValue.TryGetValue(out int status)
            || status is < 400 or > 599
            || !TryGetOptionalCount(obj["retryAfterSeconds"], out int? retryAfterSeconds)
            || !TryGetOptionalCount(obj["failCount"], out int? failCount))
        {
            return false;
        }

        fault = new FaultRequest(path, status, retryAfterSeconds, failCount);
        return true;
    }

    /// <summary>400 + 一句纯文本原因（UTF-8）。用于 feed 叠加与传输相关的端点。</summary>
    private static IResult Reject(string reason)
        => Results.Text(reason, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);

    private static readonly string[] OverlayFields =
        ["body", "version", "rolloutPercent", "mode", "packageSha256", "schemaVersion", "client"];

    private static readonly string[] ClientFields =
        ["pollIntervalSeconds", "jitterWindowSeconds", "heartbeatIntervalSeconds"];

    /// <summary>
    /// 手工取字段，理由同 <see cref="TryParseFault"/>。字段名区分大小写，未知字段一律拒绝：
    /// 把 <c>rolloutPercent</c> 拼成 <c>rolloutpercent</c> 若被悄悄忽略，测试会拿到一份"没改动"的 feed 却不知道为什么。
    /// 数值不做范围检查（rolloutPercent 给 150、pollIntervalSeconds 给负数都放行）：这台服务器的用途之一
    /// 就是喂客户端"结构合法但取值离谱"的 feed，看它怎么自保。
    /// </summary>
    private static bool TryParseOverlay(
        JsonNode? body,
        [NotNullWhen(true)] out FeedOverlayRequest? request,
        [NotNullWhen(false)] out string? error)
    {
        request = null;

        if (body is not JsonObject obj)
        {
            error = "请求体必须是 JSON 对象。";
            return false;
        }

        if (FindUnknownField(obj, OverlayFields) is { } unknown)
        {
            error = $"未知字段 \"{unknown}\"（字段名区分大小写），可用字段：{string.Join(", ", OverlayFields)}。";
            return false;
        }

        if (!TryReadString(obj, "body", out string? feedBody, out error)
            || !TryReadString(obj, "version", out string? version, out error)
            || !TryReadInt(obj, "rolloutPercent", out int? rolloutPercent, out error)
            || !TryReadString(obj, "mode", out string? mode, out error)
            || !TryReadString(obj, "packageSha256", out string? packageSha256, out error)
            || !TryReadInt(obj, "schemaVersion", out int? schemaVersion, out error)
            || !TryReadClient(obj, out ClientPolicyPatch? client, out error))
        {
            return false;
        }

        request = new FeedOverlayRequest(feedBody, version, rolloutPercent, mode, packageSha256, schemaVersion, client);
        return true;
    }

    private static bool TryReadClient(JsonObject obj, out ClientPolicyPatch? client, [NotNullWhen(false)] out string? error)
    {
        client = null;
        error = null;

        if (obj["client"] is null)
        {
            return true;
        }

        if (obj["client"] is not JsonObject segment)
        {
            error = "client 必须是对象。";
            return false;
        }

        if (FindUnknownField(segment, ClientFields) is { } unknown)
        {
            error = $"client 里有未知字段 \"{unknown}\"，可用字段：{string.Join(", ", ClientFields)}。";
            return false;
        }

        if (!TryReadInt(segment, "pollIntervalSeconds", out int? poll, out error)
            || !TryReadInt(segment, "jitterWindowSeconds", out int? jitter, out error)
            || !TryReadInt(segment, "heartbeatIntervalSeconds", out int? heartbeat, out error))
        {
            return false;
        }

        client = new ClientPolicyPatch(poll, jitter, heartbeat);
        return true;
    }

    private static string? FindUnknownField(JsonObject obj, string[] allowed)
    {
        foreach ((string name, _) in obj)
        {
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    private static bool TryReadString(JsonObject obj, string name, out string? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (obj[name] is null)
        {
            return true;
        }

        if (obj[name] is JsonValue json && json.TryGetValue(out string? text))
        {
            value = text;
            return true;
        }

        error = $"{name} 必须是字符串。";
        return false;
    }

    private static bool TryReadInt(JsonObject obj, string name, out int? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (obj[name] is null)
        {
            return true;
        }

        if (obj[name] is JsonValue json && json.TryGetValue(out int number))
        {
            value = number;
            return true;
        }

        error = $"{name} 必须是整数。";
        return false;
    }

    private static readonly string[] TransferFields = ["path", "bytesPerSecond", "cutAfterBytes"];

    /// <summary>
    /// 手工取字段，理由同 <see cref="TryParseFault"/>。<c>path</c> 必填；<c>bytesPerSecond</c>（&gt;= 0，0 = 不限速）
    /// 与 <c>cutAfterBytes</c>（&gt;= 1）至少给一个 —— 一条什么都不做的设置只会让测试作者以为自己开了开关。
    /// 字段名区分大小写，未知字段一律拒绝（理由同 feed 叠加）。
    /// </summary>
    private static bool TryParseTransfer(
        JsonNode? body,
        [NotNullWhen(true)] out TransferRequest? transfer,
        [NotNullWhen(false)] out string? error)
    {
        transfer = null;

        if (body is not JsonObject obj)
        {
            error = "请求体必须是 JSON 对象。";
            return false;
        }

        if (FindUnknownField(obj, TransferFields) is { } unknown)
        {
            error = $"未知字段 \"{unknown}\"（字段名区分大小写），可用字段：{string.Join(", ", TransferFields)}。";
            return false;
        }

        if (!TryReadString(obj, "path", out string? path, out error)
            || !TryReadInt(obj, "bytesPerSecond", out int? bytesPerSecond, out error)
            || !TryReadLong(obj, "cutAfterBytes", out long? cutAfterBytes, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path 必填，且不能为空。";
            return false;
        }

        if (bytesPerSecond is null && cutAfterBytes is null)
        {
            error = "bytesPerSecond 与 cutAfterBytes 至少要给一个，否则这条设置什么都不做。";
            return false;
        }

        if (bytesPerSecond < 0)
        {
            error = "bytesPerSecond 不能为负数（0 表示不限速）。";
            return false;
        }

        if (cutAfterBytes < 1)
        {
            error = "cutAfterBytes 必须 >= 1（不想切断就不要给这个字段）。";
            return false;
        }

        transfer = new TransferRequest(path, bytesPerSecond, cutAfterBytes);
        return true;
    }

    private static bool TryReadLong(JsonObject obj, string name, out long? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (obj[name] is null)
        {
            return true;
        }

        if (obj[name] is JsonValue json && json.TryGetValue(out long number))
        {
            value = number;
            return true;
        }

        error = $"{name} 必须是整数。";
        return false;
    }

    /// <summary>读磁盘上的 feed 原文作为叠加底稿；不存在得到 null。</summary>
    private static async Task<string?> ReadDiskFeedAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(Path.Combine(root, "releases.json"), cancellationToken);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>可选的非负整数：缺失 / null 得到 null；给了就必须是 int 且不小于 0。</summary>
    private static bool TryGetOptionalCount(JsonNode? node, out int? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue json && json.TryGetValue(out int parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
