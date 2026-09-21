namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 把表单翻译成 pack / sign 命令行 token。一律用 <c>--name=value</c> 写法：
/// 值以 <c>--</c> 开头时（如更新说明）空格写法会被解析器当成新选项，等号写法不会。
/// </summary>
internal static class CommandLineBuilder
{
    /// <summary>pack 命令固定写出的 feed 文件名。</summary>
    private const string FeedFileName = "releases.json";

    /// <summary>组装 pack 的参数；空白的可选项不写出，让 pack 自己取默认值。</summary>
    public static string[] BuildPack(PackForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> args =
        [
            "pack",
            $"--input={form.InputDirectory.Trim()}",
            $"--version={form.Version.Trim()}",
            $"--output={form.OutputDirectory.Trim()}",
            $"--mode={(form.IsMandatory ? "mandatory" : "optional")}",
        ];

        AddIfPresent(args, "package-name", form.PackageName);
        AddIfPresent(args, "channel", form.Channel);
        AddIfPresent(args, "rollout-percent", form.RolloutPercent);
        AddIfPresent(args, "min-updatable-from", form.MinUpdatableFrom);

        // 更新说明保持原样（不 Trim）：换行与缩进是作者写的；只有整段空白才视为没填。
        if (!string.IsNullOrWhiteSpace(form.Notes))
        {
            args.Add($"--notes={form.Notes}");
        }

        foreach (string line in form.Preserve.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            args.Add($"--preserve={line}");
        }

        if (form.Force)
        {
            args.Add("--force");
        }

        return [.. args];
    }

    /// <summary>组装 sign 的参数。</summary>
    public static string[] BuildSign(SignForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> args = ["sign", $"--feed={form.FeedPath.Trim()}", $"--key={form.KeyPath.Trim()}"];
        if (form.Resign)
        {
            args.Add("--resign");
        }

        return [.. args];
    }

    /// <summary>pack 写出的 feed 路径：输出目录下的 releases.json。用来在 pack 成功后预填 sign 的 feed。</summary>
    public static string DefaultFeedPath(string outputDirectory)
        => Path.Combine(outputDirectory.Trim(), FeedFileName);

    private static void AddIfPresent(List<string> args, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add($"--{name}={value.Trim()}");
        }
    }
}
