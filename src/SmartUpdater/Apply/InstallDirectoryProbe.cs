using System.Security;

namespace SimpleOneX.SmartUpdater;

/// <summary>探测结果：<see cref="FailureReason"/> 只在不可写时有值，形如 <c>UnauthorizedAccessException: ...</c>。</summary>
internal readonly record struct InstallDirectoryProbeResult(bool IsWritable, string? FailureReason);

/// <summary>
/// 试建一个临时文件再删，判断安装目录（<c>.smartupdater</c>）是否可写。
/// 只读探测，绝不提权、绝不抛出；不可写由调用方决定禁用更新并上报 <c>UpdateStage.PermissionCheck</c>。
/// </summary>
internal static class InstallDirectoryProbe
{
    public const string ProbeFilePrefix = ".probe-";

    public static InstallDirectoryProbeResult Probe(string stateDirectory)
    {
        string? probePath = null;
        try
        {
            Directory.CreateDirectory(stateDirectory);   // .smartupdater 本来就需要存在

            probePath = Path.Combine(stateDirectory, ProbeFilePrefix + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);
            return new InstallDirectoryProbeResult(IsWritable: true, FailureReason: null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            // ArgumentException / NotSupportedException：路径本身非法（空串、"con:" 之类）。同样是"这里不能写"，不能让探测抛出。
            return new InstallDirectoryProbeResult(IsWritable: false, FailureReason: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (probePath is not null)
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (IOException)
                {
                    // 探针文件残留在 .smartupdater/ 里无害，且它本身就是"目录不可写"的症状，已经通过返回值报告。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
