using System.Reflection;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>
/// 公开面登记表。新增 public 类型时，在自己的 partial 文件里加一个 public static readonly string[] 字段，
/// 不改本文件，避免多处改动落在同一个数组上。<see cref="PublicApiSurfaceTests"/> 收集全部字段做断言。
/// </summary>
internal static partial class ExpectedPublicApi
{
    /// <summary>基础层的公开面（顺序不重要）。</summary>
    public static readonly string[] Core =
    [
        nameof(UpdateMode),
        nameof(FilePolicy),
        nameof(ReleaseFeedDocument),
        nameof(ReleaseEntry),
        nameof(PackageInfo),
        nameof(ClientPolicy),
        nameof(PackageManifest),
        nameof(ManifestFile),
        nameof(UpdateStage),
        nameof(UpdateLogLevel),
        nameof(UpdateEventType),
        nameof(UpdateReport),
    ];

    public static IEnumerable<string[]> Groups()
        => typeof(ExpectedPublicApi)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string[]))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => (string[])f.GetValue(null)!);

    public static string[] All()
        => [.. Groups().SelectMany(g => g).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];
}
