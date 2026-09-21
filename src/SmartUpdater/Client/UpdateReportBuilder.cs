using System.Text;

namespace SimpleOneX.SmartUpdater;

/// <summary>构造三种上报：Updated、Failed、Heartbeat。</summary>
internal static class UpdateReportBuilder
{
    /// <summary>LogTail 的上限（UTF-8 字节）。</summary>
    public const int MaxLogTailBytes = 8 * 1024;

    /// <summary>更新成功。<c>DurationMs</c> 固定为 0。</summary>
    public static UpdateReport Updated(Guid deviceGuid, UpdateEnvironment env, Version? fromVersion, Version toVersion, long? diskFreeBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new UpdateReport
        {
            EventType = UpdateEventType.Updated,
            DeviceGuid = deviceGuid,
            MachineName = env.MachineName,
            Stage = null,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            IsSuccess = true,
            DurationMs = 0,
            DiskFreeBytes = diskFreeBytes,
            OsVersion = env.OsVersion,
            ReportedAt = now,
        };
    }

    /// <summary>更新失败。<paramref name="logTail"/> 超过 8 KB 时保留尾部再截一次，空串按 null 处理。</summary>
    public static UpdateReport Failed(
        Guid deviceGuid,
        UpdateEnvironment env,
        UpdateStage stage,
        string errorMessage,
        Version? fromVersion,
        Version? toVersion,
        long durationMs,
        long? diskFreeBytes,
        string? logTail,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new UpdateReport
        {
            EventType = UpdateEventType.Failed,
            DeviceGuid = deviceGuid,
            MachineName = env.MachineName,
            Stage = stage,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            IsSuccess = false,
            ErrorMessage = errorMessage,
            DurationMs = durationMs,
            DiskFreeBytes = diskFreeBytes,
            OsVersion = env.OsVersion,
            ReportedAt = now,
            LogTail = TruncateTail(logTail),
        };
    }

    /// <summary>心跳：<c>ToVersion</c> 填当前版本，服务端靠它统计版本分布。</summary>
    public static UpdateReport Heartbeat(Guid deviceGuid, UpdateEnvironment env, Version currentVersion, long? diskFreeBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new UpdateReport
        {
            EventType = UpdateEventType.Heartbeat,
            DeviceGuid = deviceGuid,
            MachineName = env.MachineName,
            Stage = null,
            FromVersion = null,
            ToVersion = currentVersion,
            IsSuccess = true,
            DiskFreeBytes = diskFreeBytes,
            OsVersion = env.OsVersion,
            ReportedAt = now,
        };
    }

    private static string? TruncateTail(string? tail)
    {
        if (string.IsNullOrEmpty(tail))
        {
            return null;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(tail);
        if (bytes.Length <= MaxLogTailBytes)
        {
            return tail;
        }

        int start = bytes.Length - MaxLogTailBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }

        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
