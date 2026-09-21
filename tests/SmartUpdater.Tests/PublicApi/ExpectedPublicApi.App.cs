using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal static partial class ExpectedPublicApi
{
    public static readonly string[] App = [nameof(SmartUpdaterApp), nameof(StartupResult), nameof(SingleInstanceHandle)];
}
