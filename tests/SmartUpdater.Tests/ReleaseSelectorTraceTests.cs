using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ReleaseSelectorTraceTests
{
    private static readonly Guid Device = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static SelectionContext Context(string current, IEnumerable<string>? skipped = null, bool allowDowngrade = false)
        => new(Version.Parse(current), Device, (skipped ?? []).Select(Version.Parse).ToHashSet(), allowDowngrade);

    [Fact]
    public void Every_entry_gets_a_verdict_in_descending_order_and_the_highest_reachable_is_selected()
    {
        var feed = FeedFixtures.Feed(
            FeedFixtures.Release("1.3", rolloutPercent: 0),
            FeedFixtures.Release("2.0", minUpdatableFrom: "1.5"),
            FeedFixtures.Release("1.9", minUpdatableFrom: "1.5"),     // 既低于门槛又被跳过：门槛先判
            FeedFixtures.Release("1.2.5"),
            FeedFixtures.Release("1.1"),                              // 可达但更低：仍要出现在 trace 里
            FeedFixtures.Release("1.4"));

        SelectionResult result = ReleaseSelector.Explain(feed, Context("1.2", skipped: ["1.4", "1.9"]));

        Assert.Equal(SelectionOutcome.Selected, result.Outcome);
        Assert.Equal(new Version(1, 2, 5), result.Selected!.Version);
        Assert.Same(result.Selected, result.HighestReachable);
        Assert.Equal(
            [new Version(2, 0), new Version(1, 9), new Version(1, 4), new Version(1, 3), new Version(1, 2, 5), new Version(1, 1)],
            result.Traces.Select(t => t.Entry.Version));
        Assert.Equal(
            [SelectionVerdict.BelowFloor, SelectionVerdict.BelowFloor, SelectionVerdict.Skipped, SelectionVerdict.OutOfRollout, SelectionVerdict.Reachable, SelectionVerdict.Reachable],
            result.Traces.Select(t => t.Verdict));
        Assert.Equal(new Version(1, 5), result.Traces[0].Floor);
        Assert.Null(result.Traces[4].Floor);
    }

    [Fact]
    public void Trace_carries_bucket_and_percent_for_the_rollout_log_line()
    {
        ReleaseEntry entry = FeedFixtures.Release("1.2.4", rolloutPercent: 37);

        SelectionResult result = ReleaseSelector.Explain(FeedFixtures.Feed(entry), Context("1.0"));

        SelectionTrace trace = Assert.Single(result.Traces);
        Assert.Equal(RolloutGate.BucketOf(Device, entry.Version), trace.Bucket);
        Assert.Equal(37, trace.RolloutPercent);
        Assert.Equal(RolloutGate.IsIncluded(Device, entry.Version, 37) ? SelectionVerdict.Reachable : SelectionVerdict.OutOfRollout, trace.Verdict);
    }

    [Fact]
    public void Out_of_range_percent_is_reported_as_given_but_judged_clamped()
    {
        ReleaseEntry entry = FeedFixtures.Release("1.2.4", rolloutPercent: 150);

        SelectionTrace trace = Assert.Single(ReleaseSelector.Explain(FeedFixtures.Feed(entry), Context("1.0")).Traces);

        Assert.Equal(150, trace.RolloutPercent);
        Assert.Equal(SelectionVerdict.Reachable, trace.Verdict);
    }

    [Fact]
    public void Empty_feed_yields_NoReachableEntry()
    {
        SelectionResult result = ReleaseSelector.Explain(FeedFixtures.Feed(), Context("1.0"));

        Assert.Equal(SelectionOutcome.NoReachableEntry, result.Outcome);
        Assert.Null(result.Selected);
        Assert.Null(result.HighestReachable);
        Assert.Empty(result.Traces);
    }

    [Fact]
    public void All_entries_unreachable_yields_NoReachableEntry_with_traces()
    {
        var feed = FeedFixtures.Feed(FeedFixtures.Release("2.0", minUpdatableFrom: "1.9"), FeedFixtures.Release("1.5", rolloutPercent: 0));

        SelectionResult result = ReleaseSelector.Explain(feed, Context("1.0"));

        Assert.Equal(SelectionOutcome.NoReachableEntry, result.Outcome);
        Assert.Null(result.Selected);
        Assert.Equal(2, result.Traces.Count);
        Assert.DoesNotContain(result.Traces, t => t.Verdict == SelectionVerdict.Reachable);
    }

    [Fact]
    public void Highest_reachable_equal_to_current_yields_AlreadyCurrent()
    {
        var feed = FeedFixtures.Feed(FeedFixtures.Release("1.2.4"), FeedFixtures.Release("1.2.3"));

        SelectionResult result = ReleaseSelector.Explain(feed, Context("1.2.4"));

        Assert.Equal(SelectionOutcome.AlreadyCurrent, result.Outcome);
        Assert.Null(result.Selected);
        Assert.Equal(new Version(1, 2, 4), result.HighestReachable!.Version);
    }

    [Fact]
    public void Lower_highest_reachable_depends_on_downgrade_flag()
    {
        var feed = FeedFixtures.Feed(FeedFixtures.Release("1.0.0"));

        SelectionResult denied = ReleaseSelector.Explain(feed, Context("2.0.0"));
        SelectionResult allowed = ReleaseSelector.Explain(feed, Context("2.0.0", allowDowngrade: true));

        Assert.Equal(SelectionOutcome.DowngradeNotAllowed, denied.Outcome);
        Assert.Null(denied.Selected);
        Assert.Equal(new Version(1, 0, 0), denied.HighestReachable!.Version);
        Assert.Equal(SelectionOutcome.Selected, allowed.Outcome);
        Assert.Equal(new Version(1, 0, 0), allowed.Selected!.Version);
    }

    [Fact]
    public void Select_returns_exactly_what_Explain_selects()
    {
        var feed = FeedFixtures.Feed(
            FeedFixtures.Release("3.0", minUpdatableFrom: "2.0"),
            FeedFixtures.Release("2.0", rolloutPercent: 100),
            FeedFixtures.Release("1.5", rolloutPercent: 0));

        foreach (string current in new[] { "1.0", "2.0", "2.5", "3.0", "4.0" })
        {
            foreach (bool downgrade in new[] { false, true })
            {
                SelectionContext ctx = Context(current, allowDowngrade: downgrade);
                Assert.Same(ReleaseSelector.Explain(feed, ctx).Selected, ReleaseSelector.Select(feed, ctx));
            }
        }
    }

    [Fact]
    public void Explain_rejects_null_feed()
    {
        Assert.Throws<ArgumentNullException>(() => ReleaseSelector.Explain(null!, Context("1.0")));
    }
}
