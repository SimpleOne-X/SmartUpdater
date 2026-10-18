using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal static partial class ExpectedPublicApi
{
    /// <summary>服务端签名入口。</summary>
    public static readonly string[] Signing = [nameof(ReleaseSigner)];
}
