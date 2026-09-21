using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class DiskSpaceCheckerTests
{
    private const string InstallDir = @"C:\Users\x\AppData\Local\MyApp\app";
    private const string CacheDir = @"C:\Users\x\AppData\Local\MyApp\updates";
    private const string OtherVolumeCacheDir = @"D:\cache\MyApp\updates";

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(100, 120)]
    [InlineData(80_000_000, 96_000_000)]
    public void Install_bytes_are_file_total_times_safety_factor_rounded_up(long total, long expected)
    {
        Assert.Equal(expected, DiskSpaceChecker.ComputeInstallBytes(total));
    }

    [Fact]
    public void Same_volume_requires_package_plus_install_bytes_in_one_check()
    {
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(
            packageBytes: 30_000_000,
            totalFileBytes: 80_000_000,
            InstallDir,
            CacheDir,
            availableBytesOf: _ => 1_000_000_000);

        Assert.Null(result.Cache);
        Assert.Equal(@"C:\", result.Install.Root);
        Assert.Equal(126_000_000, result.Install.RequiredBytes);
        Assert.Equal(126_000_000, result.RequiredBytes);
        Assert.Equal(1_000_000_000, result.Install.AvailableBytes);
        Assert.True(result.IsSufficient);
    }

    [Theory]
    [InlineData(126_000_000, true)]
    [InlineData(125_999_999, false)]
    public void Available_equal_to_required_is_sufficient_one_byte_less_is_not(long available, bool expected)
    {
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(30_000_000, 80_000_000, InstallDir, CacheDir, _ => available);

        Assert.Equal(expected, result.IsSufficient);
    }

    [Fact]
    public void Different_volumes_are_checked_separately()
    {
        var seen = new List<string>();
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(30_000_000, 80_000_000, InstallDir, OtherVolumeCacheDir, root =>
        {
            seen.Add(root);
            return root.StartsWith('C') ? 100_000_000 : 40_000_000;
        });

        Assert.NotNull(result.Cache);
        Assert.Equal(96_000_000, result.Install.RequiredBytes);
        Assert.Equal(30_000_000, result.Cache.Value.RequiredBytes);
        Assert.Equal(@"D:\", result.Cache.Value.Root);
        Assert.Equal(126_000_000, result.RequiredBytes);
        Assert.True(result.IsSufficient);
        Assert.Equal(new[] { @"C:\", @"D:\" }, seen);
    }

    [Fact]
    public void Insufficient_cache_volume_fails_the_whole_check()
    {
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(30_000_000, 80_000_000, InstallDir, OtherVolumeCacheDir,
            root => root.StartsWith('C') ? 1_000_000_000 : 29_999_999);

        Assert.True(result.Install.IsSufficient);
        Assert.False(result.Cache!.Value.IsSufficient);
        Assert.False(result.IsSufficient);
    }

    [Fact]
    public void Unknown_available_space_does_not_block_the_update()
    {
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(30_000_000, 80_000_000, InstallDir, CacheDir, _ => null);

        Assert.Null(result.Install.AvailableBytes);
        Assert.True(result.IsSufficient);
    }

    [Fact]
    public void Volume_root_comparison_ignores_case()
    {
        DiskSpaceCheckResult result = DiskSpaceChecker.Check(1, 1, @"c:\apps\a", @"C:\cache", _ => 10);

        Assert.Null(result.Cache);
    }

    [Fact]
    public void GetAvailableBytes_reports_a_positive_number_for_a_real_directory_and_null_for_unc()
    {
        using var dir = new TempDirectory();

        long? local = DiskSpaceChecker.GetAvailableBytes(dir.Root);
        long? unc = DiskSpaceChecker.GetAvailableBytes(@"\\server\share\dir");

        Assert.True(local > 0);
        Assert.Null(unc);
    }
}
