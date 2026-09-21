using System.Globalization;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>命令行选项。</summary>
/// <param name="Root">静态托管的根目录，必须已存在。</param>
/// <param name="Port">监听端口；0 表示由操作系统分配。</param>
internal sealed record MockServerOptions(string Root, int Port)
{
    private const string RootOption = "--root";
    private const string PortOption = "--port";

    /// <summary>
    /// 解析命令行。只认 <c>--root &lt;dir&gt;</c>（必填，目录必须已存在）与
    /// <c>--port &lt;n&gt;</c>（默认 0，取值 0..65535）；未知选项、缺值、重复选项一律失败。
    /// 成功时 <paramref name="error"/> 为 <see langword="null"/>，失败时 <paramref name="options"/> 为 <see langword="null"/>。
    /// </summary>
    internal static bool TryParse(string[] args, out MockServerOptions? options, out string? error)
    {
        options = null;
        string? root = null;
        int? port = null;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token is not (RootOption or PortOption))
            {
                error = $"未知参数: {token}";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = $"选项 {token} 缺少值。";
                return false;
            }

            string value = args[++i];

            if (token == RootOption)
            {
                if (root is not null)
                {
                    error = $"选项 {RootOption} 重复出现。";
                    return false;
                }

                if (!Directory.Exists(value))
                {
                    error = $"{RootOption} 指向的目录不存在: {value}";
                    return false;
                }

                root = value;
            }
            else
            {
                if (port is not null)
                {
                    error = $"选项 {PortOption} 重复出现。";
                    return false;
                }

                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                    || parsed is < 0 or > 65535)
                {
                    error = $"{PortOption} 必须是 0..65535 之间的整数（0 表示由操作系统分配），实际为: {value}";
                    return false;
                }

                port = parsed;
            }
        }

        if (root is null)
        {
            error = $"缺少必填选项 {RootOption} <dir>。";
            return false;
        }

        options = new MockServerOptions(root, port ?? 0);
        error = null;
        return true;
    }
}
