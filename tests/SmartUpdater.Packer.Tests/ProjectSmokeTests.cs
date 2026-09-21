using System.Text.Json;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void Packer_tests_can_see_SmartUpdater_internals()
    {
        // SmartUpdaterJsonContext 是 internal；这行能编译就说明 InternalsVisibleTo 生效。
        PackageManifest? manifest = JsonSerializer.Deserialize(
            """{"schemaVersion":1,"version":"1.2.4","files":[]}""",
            SmartUpdaterJsonContext.Default.PackageManifest);

        Assert.NotNull(manifest);
        Assert.Equal(new Version(1, 2, 4), manifest.Version);
    }

    [Fact]
    public void Exit_codes_are_distinct()
    {
        int[] codes = [ExitCode.Success, ExitCode.Usage, ExitCode.Input, ExitCode.Conflict, ExitCode.Unexpected];
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
