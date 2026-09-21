using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// <c>sign</c> 命令：给 releases.json 里的条目签名。它与 <c>pack</c> 拆开是因为安全边界不同——打包可以在任何 CI 机器上跑，
/// 签名必须在能访问私钥的地方跑。本命令只负责"给我一个 key 文件我就签"，不做密钥管理，也不提供密钥生成命令。
/// <para>只往条目里加 / 换 <c>signature</c> 字段：走 <see cref="JsonNode"/> DOM，其余字段与未知字段逐字保留；
/// 但空白会被归一到 <see cref="PackerJson.Write"/>（<c>pack</c> 产出的 feed 因此除新增的 signature 行外逐字不变）。</para>
/// <para>先把所有条目都解析并签好，全部成功才落盘；任一条目出错则整体失败、feed 一字不改。
/// 退出码：选项写错走 <see cref="ExitCode.Usage"/>；feed / 私钥缺失或内容非法走 <see cref="ExitCode.Input"/>；读写磁盘失败走 <see cref="ExitCode.Unexpected"/>。</para>
/// </summary>
internal static class SignCommand
{
    /// <summary>sign 认识的全部选项名（不带 <c>--</c>）。与 <see cref="HelpText.SignUsage"/> 的选项表由测试对账。</summary>
    internal static IReadOnlyList<string> KnownOptions { get; } = ["feed", "key", "resign"];

    public static int Run(ParsedCommandLine parsed, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? unknown = parsed.FindUnknownOption(KnownOptions);
        if (unknown is not null)
        {
            error.WriteLine($"未知选项 --{unknown}。");
            error.WriteLine(HelpText.SignUsage);
            return ExitCode.Usage;
        }

        string? feedPath = SingleValue(parsed, "feed");
        if (feedPath is null)
        {
            error.WriteLine("缺少必填选项 --feed <releases.json>。");
            return ExitCode.Usage;
        }

        string? keyPath = SingleValue(parsed, "key");
        if (keyPath is null)
        {
            error.WriteLine("缺少必填选项 --key <private.pem>。");
            return ExitCode.Usage;
        }

        bool resign = parsed.HasFlag("resign");

        try
        {
            return Execute(feedPath, keyPath, resign, output, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"读写磁盘失败：{ex.Message}");
            return ExitCode.Unexpected;
        }
    }

    private static string? SingleValue(ParsedCommandLine parsed, string name)
    {
        IReadOnlyList<string> values = parsed.GetValues(name);
        return values.Count == 0 ? null : values[^1];
    }

    private static int Execute(string feedPath, string keyPath, bool resign, TextWriter output, TextWriter error)
    {
        if (!File.Exists(feedPath))
        {
            error.WriteLine($"--feed 指定的文件不存在：{feedPath}");
            return ExitCode.Input;
        }

        if (!File.Exists(keyPath))
        {
            error.WriteLine($"--key 指定的文件不存在：{keyPath}");
            return ExitCode.Input;
        }

        ECDsa key;
        try
        {
            key = ReleaseSignature.ImportPrivateKeyPem(File.ReadAllText(keyPath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            error.WriteLine($"--key 不是可用的 ECDSA P-256 PEM 私钥：{keyPath}（{ex.Message}）");
            return ExitCode.Input;
        }

        using (key)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(feedPath, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                error.WriteLine($"--feed 不是合法的 JSON：{feedPath}（{ex.Message}）");
                return ExitCode.Input;
            }

            if (root is not JsonObject rootObject || rootObject["releases"] is not JsonArray releases)
            {
                error.WriteLine($"--feed 里没有 releases 数组：{feedPath}");
                return ExitCode.Input;
            }

            // 第一遍：全部解析 + 全部签名，一次报全所有问题；都成功才动 DOM 与磁盘。
            var pending = new List<(JsonObject Node, string Version, string Signature)>();
            var problems = new List<string>();
            for (int i = 0; i < releases.Count; i++)
            {
                if (releases[i] is not JsonObject node)
                {
                    problems.Add($"第 {i + 1} 条：不是 JSON 对象。");
                    continue;
                }

                if (!resign && node["signature"] is JsonValue existing
                    && existing.TryGetValue(out string? current) && !string.IsNullOrEmpty(current))
                {
                    continue;
                }

                string label = node["version"] is JsonValue v && v.TryGetValue(out string? text) ? text : "?";
                try
                {
                    ReleaseEntry entry = JsonSerializer.Deserialize(node.ToJsonString(), SmartUpdaterJsonContext.Default.ReleaseEntry)
                        ?? throw new JsonException("条目为空。");
                    pending.Add((node, label, ReleaseSignature.Sign(entry, key)));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
                {
                    problems.Add($"第 {i + 1} 条（version={label}）：{ex.Message}");
                }
            }

            if (problems.Count > 0)
            {
                error.WriteLine($"有 {problems.Count} 条条目无法签名，feed 未改动：");
                foreach (string problem in problems)
                {
                    error.WriteLine($"  {problem}");
                }

                return ExitCode.Input;
            }

            if (pending.Count > 0)
            {
                foreach ((JsonObject node, _, string signature) in pending)
                {
                    node["signature"] = signature;
                }

                PackerJson.WriteAtomic(feedPath, Encoding.UTF8.GetBytes(rootObject.ToJsonString(PackerJson.Write)));
            }

            output.WriteLine($"{pending.Count} 条已签名{(pending.Count > 0 ? "：" + string.Join("、", pending.Select(p => p.Version)) : string.Empty)}。");
            output.WriteLine("公钥（配到 UpdateClientOptions.PublicKey）：");
            output.WriteLine(ReleaseSignature.ExportPublicKey(key));
            return ExitCode.Success;
        }
    }
}
