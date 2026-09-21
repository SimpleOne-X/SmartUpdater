using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>源生成上下文的共享 <see cref="JsonSerializerOptions"/>，仅供测试项目使用。</summary>
/// <remarks>仅测试使用，产品代码一律走 <c>SmartUpdaterJsonContext.Default.X</c>。</remarks>
internal static class SmartUpdaterJson
{
    /// <summary>
    /// 基于源生成上下文的配置副本。包内代码不要把它传给反射式的 <c>JsonSerializer</c> 重载
    /// （在 AOT 兼容项目里会报 IL2026 / IL3050），一律用 <c>SmartUpdaterJsonContext.Default.X</c>。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(SmartUpdaterJsonContext.Default.Options);
}
