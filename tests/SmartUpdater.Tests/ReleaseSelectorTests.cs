using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseSelectorTests
{
    private static ReleaseEntry Entry(
        string version,
        string? minUpdatableFrom = null,
        int rolloutPercent = 100)
        => new()
        {
            Version = Version.Parse(version),
            ReleasedAt = DateTimeOffset.UnixEpoch,
            Package = new PackageInfo { Url = $"p-{version}.zip", Size = 1, Sha256 = "h" },
            MinUpdatableFrom = minUpdatableFrom is null ? null : Version.Parse(minUpdatableFrom),
            RolloutPercent = rolloutPercent,
        };

    private static ReleaseFeedDocument Feed(params ReleaseEntry[] releases)
        => new() { SchemaVersion = 1, Releases = releases };

    private static SelectionContext Context(
        string current,
        IEnumerable<string>? skipped = null,
        bool allowDowngrade = false)
        => new(
            CurrentVersion: Version.Parse(current),
            DeviceGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            SkippedVersions: (skipped ?? []).Select(Version.Parse).ToHashSet(),
            AllowVersionDowngrade: allowDowngrade);

    [Fact]
    public void Picks_newest_entry_that_is_higher_than_current()
    {
        var feed = Feed(Entry("1.2.4"), Entry("1.2.3"), Entry("1.2.2"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.2"));

        Assert.Equal(new Version(1, 2, 4), picked!.Version);
    }

    [Fact]
    public void Returns_null_when_already_on_newest()
    {
        var feed = Feed(Entry("1.2.4"), Entry("1.2.3"));

        Assert.Null(ReleaseSelector.Select(feed, Context("1.2.4")));
    }

    [Fact]
    public void Returns_null_when_current_is_newer_than_everything()
    {
        var feed = Feed(Entry("1.2.4"));

        Assert.Null(ReleaseSelector.Select(feed, Context("2.0.0")));
    }

    [Fact]
    public void Downgrade_is_rejected_by_default()
    {
        var feed = Feed(Entry("1.0.0"));

        Assert.Null(ReleaseSelector.Select(feed, Context("2.0.0")));
    }

    [Fact]
    public void Downgrade_is_allowed_when_opted_in()
    {
        var feed = Feed(Entry("1.0.0"));

        var picked = ReleaseSelector.Select(feed, Context("2.0.0", allowDowngrade: true));

        Assert.Equal(new Version(1, 0, 0), picked!.Version);
    }

    [Fact]
    public void Skipped_version_is_not_picked()
    {
        var feed = Feed(Entry("1.2.4"), Entry("1.2.3"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.2", skipped: ["1.2.4"]));

        Assert.Equal(new Version(1, 2, 3), picked!.Version);
    }

    [Fact]
    public void Entry_out_of_rollout_is_skipped_and_older_one_is_considered()
    {
        // 0% 灰度 = 谁都不命中，应回落到下一条
        var feed = Feed(Entry("1.2.4", rolloutPercent: 0), Entry("1.2.3"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.2"));

        Assert.Equal(new Version(1, 2, 3), picked!.Version);
    }

    [Fact]
    public void Entry_requiring_higher_floor_is_skipped_and_stepping_stone_is_picked()
    {
        // 1.2.4 要求先升到 1.2.0；当前 1.0.0 够不着，应落到 1.2.0 当跳板
        var feed = Feed(
            Entry("1.2.4", minUpdatableFrom: "1.2.0"),
            Entry("1.2.0", minUpdatableFrom: "1.0.0"));

        var picked = ReleaseSelector.Select(feed, Context("1.0.0"));

        Assert.Equal(new Version(1, 2, 0), picked!.Version);
    }

    [Fact]
    public void After_installing_the_stepping_stone_the_top_entry_becomes_reachable()
    {
        var feed = Feed(
            Entry("1.2.4", minUpdatableFrom: "1.2.0"),
            Entry("1.2.0", minUpdatableFrom: "1.0.0"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.0"));

        Assert.Equal(new Version(1, 2, 4), picked!.Version);
    }

    [Fact]
    public void Returns_null_when_no_entry_is_reachable()
    {
        // 全部条目都要求 2.0 以上，而当前是 1.0
        var feed = Feed(
            Entry("3.0.0", minUpdatableFrom: "2.0.0"),
            Entry("2.5.0", minUpdatableFrom: "2.0.0"));

        Assert.Null(ReleaseSelector.Select(feed, Context("1.0.0")));
    }

    [Fact]
    public void Feed_order_does_not_matter()
    {
        // 即便服务端没按降序排，也要挑出最高的可达版本
        var feed = Feed(Entry("1.2.2"), Entry("1.2.4"), Entry("1.2.3"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.1"));

        Assert.Equal(new Version(1, 2, 4), picked!.Version);
    }

    [Fact]
    public void Empty_feed_returns_null()
    {
        Assert.Null(ReleaseSelector.Select(Feed(), Context("1.0.0")));
    }

    [Fact]
    public void Max_chain_length_is_five()
    {
        Assert.Equal(5, ReleaseSelector.MaxChainLength);
    }

    [Fact]
    public void Downgrade_mode_does_not_move_a_client_that_is_already_on_the_newest_reachable_entry()
    {
        // 允许降级时，已经在最高可达版本上的客户端不得被推到次高版本
        // （否则下一轮又升回来，永远来回横跳）
        var feed = Feed(Entry("1.2.3"), Entry("1.2.2"));

        Assert.Null(ReleaseSelector.Select(feed, Context("1.2.3", allowDowngrade: true)));
    }

    [Fact]
    public void Downgrade_mode_rolls_back_once_and_then_settles()
    {
        // 服务端回滚：坏版本 1.2.4 已从 feed 下线，最新条目是 1.2.3
        var feed = Feed(Entry("1.2.3"), Entry("1.2.2"));

        var first = ReleaseSelector.Select(feed, Context("1.2.4", allowDowngrade: true));
        Assert.Equal(new Version(1, 2, 3), first!.Version);

        Assert.Null(ReleaseSelector.Select(feed, Context("1.2.3", allowDowngrade: true)));
    }

    [Fact]
    public void Downgrade_mode_never_oscillates()
    {
        var feed = Feed(
            Entry("1.2.4", minUpdatableFrom: "1.2.0"),
            Entry("1.2.3"),
            Entry("1.2.0", minUpdatableFrom: "1.0.0"),
            Entry("1.0.0"));

        foreach (string start in new[] { "0.9.0", "1.0.0", "1.2.0", "1.2.3", "1.2.4", "2.0.0" })
        {
            var current = Version.Parse(start);
            bool settled = false;

            // 每一步都把 Select 的结果当作新的当前版本；条目只有 4 条，任何会收敛的链最多走 4 步
            for (int step = 0; step <= feed.Releases.Count; step++)
            {
                var picked = ReleaseSelector.Select(feed, Context(current.ToString(), allowDowngrade: true));
                if (picked is null)
                {
                    settled = true;
                    break;
                }

                current = picked.Version;
            }

            Assert.True(settled, $"从 {start} 出发，允许降级时没有收敛");
        }
    }

    [Fact]
    public void Downgrade_mode_still_upgrades_to_the_top_reachable_entry()
    {
        var feed = Feed(Entry("1.2.4"), Entry("1.2.3"));

        var picked = ReleaseSelector.Select(feed, Context("1.0.0", allowDowngrade: true));

        Assert.Equal(new Version(1, 2, 4), picked!.Version);
    }

    [Fact]
    public void Downgrade_target_must_still_pass_rollout_condition()
    {
        // 回滚目标同样要过灰度条件：1.2.3 未命中灰度（0%），应落到 1.2.2
        var feed = Feed(Entry("1.2.3", rolloutPercent: 0), Entry("1.2.2"), Entry("1.2.1"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.4", allowDowngrade: true));

        Assert.Equal(new Version(1, 2, 2), picked!.Version);
    }

    [Fact]
    public void Downgrade_target_must_still_pass_skip_condition()
    {
        // 回滚目标同样要过"未被使用者跳过"条件：1.2.3 已被跳过，应落到 1.2.2
        var feed = Feed(Entry("1.2.3"), Entry("1.2.2"), Entry("1.2.1"));

        var picked = ReleaseSelector.Select(feed, Context("1.2.4", skipped: ["1.2.3"], allowDowngrade: true));

        Assert.Equal(new Version(1, 2, 2), picked!.Version);
    }
}
