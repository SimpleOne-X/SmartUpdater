using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class RolloutGateTests
{
    private static readonly Version V = new(1, 2, 4);

    [Fact]
    public void Zero_percent_includes_nobody()
    {
        for (int i = 0; i < 200; i++)
        {
            Assert.False(RolloutGate.IsIncluded(Guid.NewGuid(), V, 0));
        }
    }

    [Fact]
    public void Hundred_percent_includes_everybody()
    {
        for (int i = 0; i < 200; i++)
        {
            Assert.True(RolloutGate.IsIncluded(Guid.NewGuid(), V, 100));
        }
    }

    [Fact]
    public void Same_device_and_version_always_lands_in_same_bucket()
    {
        var device = Guid.Parse("11111111-2222-3333-4444-555555555555");

        int first = RolloutGate.BucketOf(device, V);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(first, RolloutGate.BucketOf(device, V));
        }
    }

    [Fact]
    public void Bucket_is_stable_across_process_restarts()
    {
        // 固定 GUID + 固定版本必须得到固定桶号：把算法钉死，防止有人改成 string.GetHashCode()
        // （对 string 逐进程随机化，重启即变）。
        // 期望值由 PowerShell 独立计算，与本实现无关：
        //   SHA-256(UTF-8("11111111-2222-3333-4444-555555555555|1.2.4")) 的前 4 字节按小端解释为 uint32
        //   = 1638388840，% 100 = 40；"…|2.0.0" → 2062717156 → 56。
        var device = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(40, RolloutGate.BucketOf(device, new Version(1, 2, 4)));
        Assert.Equal(56, RolloutGate.BucketOf(device, new Version(2, 0, 0)));
    }

    [Fact]
    public void Device_included_at_low_percent_stays_included_when_ramping_up()
    {
        var devices = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var d in devices)
        {
            for (int percent = 0; percent < 100; percent++)
            {
                if (RolloutGate.IsIncluded(d, V, percent))
                {
                    // 一旦命中，之后每个更大的百分比都必须继续命中
                    for (int higher = percent; higher <= 100; higher++)
                    {
                        Assert.True(RolloutGate.IsIncluded(d, V, higher));
                    }

                    break;
                }
            }
        }
    }

    [Fact]
    public void Different_versions_shuffle_the_same_device()
    {
        // 同一设备在不同版本上应落在不同桶，否则"运气差"的设备永远排在最后一批
        var device = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var buckets = new[]
        {
            RolloutGate.BucketOf(device, new Version(1, 0, 0)),
            RolloutGate.BucketOf(device, new Version(2, 0, 0)),
            RolloutGate.BucketOf(device, new Version(3, 0, 0)),
            RolloutGate.BucketOf(device, new Version(4, 0, 0)),
        };

        Assert.True(buckets.Distinct().Count() > 1);
    }

    [Fact]
    public void Distribution_is_roughly_uniform()
    {
        var devices = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray();

        int included = devices.Count(d => RolloutGate.IsIncluded(d, V, 25));

        // 2000 台里取 25%，允许 ±5 个百分点的抽样波动
        Assert.InRange(included, 400, 600);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Out_of_range_percent_is_clamped(int percent)
    {
        var device = Guid.NewGuid();

        bool included = RolloutGate.IsIncluded(device, V, percent);

        Assert.Equal(percent > 100, included);
    }

    [Theory]
    [InlineData(1, 2, 4, 40)]
    [InlineData(2, 0, 0, 56)]
    public void Percent_equal_to_bucket_does_not_include_but_one_more_does(int major, int minor, int build, int bucket)
    {
        // 判定是严格小于（桶号 % 100 < rolloutPercent）。边界必须确定性地钉住：
        // 百分比恰等于桶号时不命中，加一才命中。写成 <= 的话，rolloutPercent 为 0
        // （"先发布、暂不放量"）时桶号为 0 的设备（约 1%）会被放量。
        // 桶号取自 Bucket_is_stable_across_process_restarts 的钉死向量：同一设备，1.2.4 → 40，2.0.0 → 56。
        var device = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var version = new Version(major, minor, build);

        Assert.False(RolloutGate.IsIncluded(device, version, bucket));
        Assert.True(RolloutGate.IsIncluded(device, version, bucket + 1));
    }

    [Fact]
    public void Bucket_is_pinned_for_device_guid_with_hex_letters()
    {
        // Bucket_is_stable_across_process_restarts 的 GUID 只含十进制数字，大小写无从体现：
        // 哈希输入里的 GUID 被改成大写后它仍然通过。这里用全字母的 GUID 把"小写 D 格式"钉死。
        // 期望值由 PowerShell 与 sha256sum 各自独立计算，与本实现无关：
        //   SHA-256(UTF-8("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee|1.2.4")) 的前 4 字节按小端解释为 uint32
        //   = 1379043409，% 100 = 9；对照：大写的 "AAAAAAAA-…|1.2.4" → 3407785534 → 34。
        var device = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(9, RolloutGate.BucketOf(device, new Version(1, 2, 4)));
    }

    // 版本号是桶号的哈希输入。null 会被 $"{version}" 插值成空串而静默算出一个桶号，
    // 把上游的数据错误（例如 feed 里 "version": null 能通过 required）藏成"这台设备碰巧命中或没命中"，必须快速失败。
    [Fact]
    public void IsIncluded_with_null_version_throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RolloutGate.IsIncluded(Guid.NewGuid(), null!, 50));

        Assert.Equal("version", ex.ParamName);
    }

    [Fact]
    public void BucketOf_with_null_version_throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RolloutGate.BucketOf(Guid.NewGuid(), null!));

        Assert.Equal("version", ex.ParamName);
    }
}
