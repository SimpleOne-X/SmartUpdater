namespace SimpleOneX.SmartUpdater;

/// <summary>manifest 路径的安全校验。zip 条目名从不当路径用，只有通过这里的 manifest 路径才会被写盘。</summary>
internal static class ManifestPathValidator
{
    private const string StateDirectoryName = ".smartupdater";

    /// <summary>校验整份文件列表，返回每条问题（含路径与原因）；空列表表示全部合法。不抛异常（<paramref name="files"/> 为 null 除外）。</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ManifestFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestFile? file in files)
        {
            if (file is null)
            {
                errors.Add("文件列表含空条目");
                continue;
            }

            if (!IsSafeRelativePath(file.Path, out string? reason))
            {
                errors.Add($"路径 '{file.Path}'：{reason}");
                continue;
            }

            if (!seen.Add(file.Path))
            {
                errors.Add($"路径 '{file.Path}'：重复路径（不分大小写）");
            }
        }

        return errors;
    }

    /// <summary>单条路径是否是安全的相对路径。<paramref name="reason"/> 是第一条被违反的规则，合法时为 null。</summary>
    public static bool IsSafeRelativePath(string path, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "路径为空";
            return false;
        }

        if (path.Contains('\\'))
        {
            reason = "含反斜杠";
            return false;
        }

        if (path.StartsWith('/'))
        {
            reason = "绝对路径";
            return false;
        }

        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                reason = "空路径段";
                return false;
            }

            if (segment is "." or "..")
            {
                reason = "含 . 或 .. 段";
                return false;
            }

            if (WindowsFileNames.HasInvalidCharacters(segment))
            {
                reason = "含非法字符";
                return false;
            }

            if (WindowsFileNames.EndsWithDotOrSpace(segment))
            {
                reason = "路径段以点或空格结尾";
                return false;
            }

            if (WindowsFileNames.IsReservedDeviceName(segment))
            {
                reason = "保留设备名";
                return false;
            }
        }

        if (string.Equals(segments[0], StateDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "位于更新器状态目录";
            return false;
        }

        if (segments.Any(SwapFileNames.HasSwapSuffix))
        {
            reason = "使用了更新器保留后缀";
            return false;
        }

        reason = null;
        return true;
    }
}
