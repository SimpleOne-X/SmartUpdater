using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>唯一需要的动态端点。</summary>
internal static class ReportEndpoint
{
    public static void Map(WebApplication app, ControlState state)
    {
        app.MapPost("/api/v1/update-reports", async (HttpContext context) =>
        {
            JsonNode? node;
            try
            {
                node = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (node is not JsonObject report)
            {
                return Results.BadRequest();
            }

            state.AddReport(report);
            Console.WriteLine(FormatLine(report));

            return Results.Accepted();
        });
    }

    /// <summary>
    /// 控制台上的那一行：<c>REPORT &lt;eventType&gt; &lt;json&gt;</c>，恒为单行，便于脚本 grep。
    /// 客户端发来什么形状都照收，所以 eventType 缺失、为 null、不是字符串、含控制字符时都不能让打印本身出错或折行。
    /// </summary>
    internal static string FormatLine(JsonObject report) => $"REPORT {DescribeEventType(report)} {report.ToJsonString()}";

    private static string DescribeEventType(JsonObject report) => report["eventType"] switch
    {
        null => "<none>",
        JsonValue value when value.TryGetValue(out string? text) && !text.Any(char.IsControl) => text,

        // 非字符串，或字符串里带换行等控制字符：退回 JSON 文本（带引号、控制字符已转义），保证单行。
        JsonNode other => other.ToJsonString(),
    };
}
