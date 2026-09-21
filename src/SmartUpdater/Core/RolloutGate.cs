using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>按设备标识与版本做稳定的灰度判定。</summary>
internal static class RolloutGate
{
    /// <summary>判断本设备是否在该版本的灰度批次内。</summary>
    /// <param name="deviceGuid">设备唯一标识。</param>
    /// <param name="version">候选版本。</param>
    /// <param name="rolloutPercent">灰度百分比，超出 0~100 会被钳制。</param>
    public static bool IsIncluded(Guid deviceGuid, Version version, int rolloutPercent)
    {
        ArgumentNullException.ThrowIfNull(version);

        int percent = Math.Clamp(rolloutPercent, 0, 100);
        return BucketOf(deviceGuid, version) < percent;
    }

    /// <summary>计算本设备在该版本上的桶号，范围 0~99。</summary>
    /// <remarks>
    /// 用 SHA-256 而非 <see cref="object.GetHashCode"/>：后者对字符串是逐进程随机化的，
    /// 设备重启后桶号会变，导致已更新的设备被判为未命中而反复横跳。
    /// 把版本号混入哈希，是为了让同一设备在不同版本上落到不同桶——
    /// 否则"运气差"的设备会永远排在每一次放量的最后一批。
    /// </remarks>
    public static int BucketOf(Guid deviceGuid, Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string material = $"{deviceGuid:D}|{version}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);

        // 取前 4 字节按小端解释为无符号整数，再取模；用 uint 而不是 int，避免出现负数取模。
        // 显式按小端读取，与本机字节序无关，桶号因此在各设备上一致。
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(hash);
        return (int)(value % 100u);
    }
}
