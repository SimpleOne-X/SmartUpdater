namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>一次出现的选项。Value 为 null 表示它是个开关（没有跟值）。</summary>
internal sealed record ParsedOption(string Name, string? Value);

/// <summary>解析后的命令行。按出现顺序保留全部选项，重复出现的选项不合并。</summary>
internal sealed record ParsedCommandLine(string Command, IReadOnlyList<ParsedOption> Options)
{
    /// <summary>该选项是否出现过（无论有没有跟值）。</summary>
    public bool HasOption(string name)
        => Options.Any(o => string.Equals(o.Name, name, StringComparison.Ordinal));

    /// <summary>该选项是否作为开关出现过（出现且没有跟值）。</summary>
    public bool HasFlag(string name)
        => Options.Any(o => string.Equals(o.Name, name, StringComparison.Ordinal) && o.Value is null);

    /// <summary>该选项的全部值，按出现顺序；开关形式的出现不计入。</summary>
    public IReadOnlyList<string> GetValues(string name)
        => [.. Options.Where(o => string.Equals(o.Name, name, StringComparison.Ordinal) && o.Value is not null)
                      .Select(o => o.Value!)];

    /// <summary>
    /// 返回第一个不在 known 里的选项名；全部已知时返回 null。
    /// 返回的名字不带 <c>--</c> 前缀；调用方拼进错误信息时要自己补上（<c>$"未知选项 --{name}"</c>）。
    /// </summary>
    public string? FindUnknownOption(IReadOnlyCollection<string> known)
        => Options.FirstOrDefault(o => !known.Contains(o.Name, StringComparer.Ordinal))?.Name;
}
