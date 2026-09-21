using System.Globalization;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 表单的"点按钮前"校验：只查界面层能确定的问题（必填、格式），一次返回全部错误。
/// 目录是否真的存在、feed 是否冲突等要碰磁盘的判断留给 pack / sign 本身，由退出码报告。
/// </summary>
internal static class FormValidator
{
    /// <summary>校验 pack 表单；返回空列表表示通过。</summary>
    public static IReadOnlyList<string> Validate(PackForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(form.InputDirectory))
        {
            errors.Add("请选择应用目录。");
        }

        if (string.IsNullOrWhiteSpace(form.OutputDirectory))
        {
            errors.Add("请选择输出目录。");
        }

        if (!IsFourSegmentVersion(form.Version))
        {
            errors.Add("版本号必须是四段数字，如 1.2.4.0。");
        }

        if (!string.IsNullOrWhiteSpace(form.MinUpdatableFrom) && !Version.TryParse(form.MinUpdatableFrom.Trim(), out _))
        {
            errors.Add("最低可升级版本不是合法的版本号（2 到 4 段数字）。");
        }

        if (!string.IsNullOrWhiteSpace(form.RolloutPercent) && !IsPercent(form.RolloutPercent))
        {
            errors.Add("灰度百分比必须是 0 到 100 的整数。");
        }

        return errors;
    }

    /// <summary>校验 sign 表单；返回空列表表示通过。</summary>
    public static IReadOnlyList<string> Validate(SignForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(form.FeedPath))
        {
            errors.Add("请选择要签名的 releases.json。");
        }

        if (string.IsNullOrWhiteSpace(form.KeyPath))
        {
            errors.Add("请选择私钥文件。");
        }

        return errors;
    }

    private static bool IsFourSegmentVersion(string text)
    {
        string trimmed = text.Trim();
        string[] segments = trimmed.Split('.');
        return segments.Length == 4
            && segments.All(s => s.Length > 0 && s.All(char.IsAsciiDigit))
            && Version.TryParse(trimmed, out _);
    }

    private static bool IsPercent(string text)
        => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent <= 100;
}
