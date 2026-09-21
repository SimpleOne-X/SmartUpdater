using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// feed 字节 → <see cref="ReleaseFeedDocument"/> 的 JSON 层解析。
/// 与整份文档直接反序列化的区别：releases 逐条反序列化，坏条目跳过并记入 <see cref="ReleaseFeedDocument.ParseWarnings"/>，
/// 不让一条非法版本串拖垮整份 feed。语义校验在 <see cref="ReleaseFeedValidator"/>。
/// </summary>
internal static class ReleaseFeedReader
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>解析 feed。根不是 JSON 对象、语法错误、空输入 → <see cref="JsonException"/>。</summary>
    public static ReleaseFeedDocument Parse(ReadOnlyMemory<byte> utf8Json)
    {
        // 记事本保存的 UNC feed 常带 BOM，而 JsonDocument.Parse 对 EF BB BF 开头的字节直接抛异常（实测）
        if (utf8Json.Span.StartsWith(Utf8Bom))
        {
            utf8Json = utf8Json[Utf8Bom.Length..];
        }

        using JsonDocument document = JsonDocument.Parse(utf8Json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"feed 的根必须是 JSON 对象，实际是 {root.ValueKind}。");
        }

        var warnings = new List<string>();

        int schemaVersion = 0;
        if (root.TryGetProperty("schemaVersion", out JsonElement schemaElement)
            && schemaElement.ValueKind == JsonValueKind.Number
            && schemaElement.TryGetInt32(out int parsedSchema))
        {
            schemaVersion = parsedSchema;
        }

        string? channel = null;
        if (root.TryGetProperty("channel", out JsonElement channelElement) && channelElement.ValueKind != JsonValueKind.Null)
        {
            if (channelElement.ValueKind == JsonValueKind.String)
            {
                channel = channelElement.GetString();
            }
            else
            {
                warnings.Add($"channel: 期望字符串，实际是 {channelElement.ValueKind}，已忽略。");
            }
        }

        ClientPolicy? client = null;
        if (root.TryGetProperty("client", out JsonElement clientElement) && clientElement.ValueKind != JsonValueKind.Null)
        {
            if (clientElement.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    client = clientElement.Deserialize(SmartUpdaterJsonContext.Default.ClientPolicy);
                }
                catch (JsonException ex)
                {
                    warnings.Add($"client: {ex.Message} 已忽略，沿用本地配置。");
                }
            }
            else
            {
                warnings.Add($"client: 期望对象，实际是 {clientElement.ValueKind}，已忽略，沿用本地配置。");
            }
        }

        var releases = new List<ReleaseEntry>();
        if (root.TryGetProperty("releases", out JsonElement releasesElement))
        {
            if (releasesElement.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in releasesElement.EnumerateArray())
                {
                    try
                    {
                        ReleaseEntry? entry = item.Deserialize(SmartUpdaterJsonContext.Default.ReleaseEntry);
                        if (entry is null)
                        {
                            warnings.Add($"releases[{index}]: 为 null，已跳过。");
                        }
                        else
                        {
                            releases.Add(entry);
                        }
                    }
                    catch (JsonException ex)
                    {
                        warnings.Add($"releases[{index}]: {ex.Message} 已跳过该条目。");
                    }

                    index++;
                }
            }
            else
            {
                warnings.Add($"releases: 期望数组，实际是 {releasesElement.ValueKind}，按空列表处理。");
            }
        }

        return new ReleaseFeedDocument
        {
            SchemaVersion = schemaVersion,
            Channel = channel,
            Client = client,
            Releases = releases,
            ParseWarnings = warnings,
        };
    }
}
