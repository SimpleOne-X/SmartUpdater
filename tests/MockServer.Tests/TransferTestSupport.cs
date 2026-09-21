using System.Text;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>限速 / 切断相关测试共用的小工具。</summary>
internal static class TransferTestSupport
{
    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static byte[] MakePackage(int size)
    {
        byte[] bytes = new byte[size];
        new Random(4242).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 读响应体直到结束或连接出错，返回已经收到的字节，以及出错时的异常（正常读完则为 null）。
    /// 取消不在此列：取消一律向外抛，免得把测试超时吞成"读到一半"。
    /// </summary>
    public static async Task<(byte[] Received, Exception? Failure)> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        Exception? failure = null;
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            failure = e;
        }

        return (received.ToArray(), failure);
    }
}
