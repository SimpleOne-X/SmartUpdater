using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class UpdateEventArgsTests
{
    private static UpdateAvailableEventArgs Available()
        => new(FeedFixtures.Release("1.2.4", UpdateMode.Optional, notes: "修复", size: 123), new Version(1, 2, 3, 0));

    [Fact]
    public void Available_exposes_the_release_fields()
    {
        UpdateAvailableEventArgs e = Available();

        Assert.Equal(new Version(1, 2, 4), e.Version);
        Assert.Equal(new Version(1, 2, 3, 0), e.CurrentVersion);
        Assert.Equal(UpdateMode.Optional, e.Mode);
        Assert.Equal("修复", e.Notes);
        Assert.Equal(123, e.PackageSize);
        Assert.Equal(FeedFixtures.ReleasedAt, e.ReleasedAt);
        Assert.Equal(UpdateDecision.Undecided, e.Decision);
    }

    [Fact]
    public void Accept_records_the_decision()
    {
        UpdateAvailableEventArgs e = Available();
        e.Accept();
        Assert.Equal(UpdateDecision.Accept, e.Decision);
    }

    [Fact]
    public void Postpone_records_the_decision()
    {
        UpdateAvailableEventArgs e = Available();
        e.Postpone();
        Assert.Equal(UpdateDecision.Postpone, e.Decision);
    }

    [Fact]
    public void Skip_records_the_decision()
    {
        UpdateAvailableEventArgs e = Available();
        e.Skip();
        Assert.Equal(UpdateDecision.Skip, e.Decision);
    }

    [Fact]
    public void Second_decision_throws_and_keeps_the_first()
    {
        UpdateAvailableEventArgs e = Available();
        e.Accept();

        Assert.Throws<InvalidOperationException>(e.Skip);
        Assert.Equal(UpdateDecision.Accept, e.Decision);
    }

    [Fact]
    public void Deciding_after_seal_throws()
    {
        UpdateAvailableEventArgs e = Available();
        e.Seal();

        Assert.Throws<InvalidOperationException>(e.Accept);
        Assert.Throws<InvalidOperationException>(e.Postpone);
        Assert.Throws<InvalidOperationException>(e.Skip);
        Assert.Equal(UpdateDecision.Undecided, e.Decision);
    }

    [Fact]
    public void Restarting_collects_tasks_until_sealed()
    {
        var e = new RestartingEventArgs(new Version(1, 2, 4, 0));
        var t1 = new TaskCompletionSource().Task;
        var t2 = Task.CompletedTask;

        e.WaitFor(t1);
        e.WaitFor(t2);

        Assert.Equal(new Version(1, 2, 4, 0), e.Version);
        Assert.Equal([t1, t2], e.PendingTasks);
        e.Seal();
        Assert.Throws<InvalidOperationException>(() => e.WaitFor(Task.CompletedTask));
        Assert.Equal(2, e.PendingTasks.Count);
    }

    [Fact]
    public void Restarting_rejects_null_task()
    {
        var e = new RestartingEventArgs(new Version(1, 0, 0, 0));

        Assert.Throws<ArgumentNullException>(() => e.WaitFor(null!));
    }

    [Fact]
    public void Progress_computes_percent_and_clamps()
    {
        Assert.Equal(25.0, new UpdateProgressEventArgs(UpdateStage.Download, 250, 1000, 5.5).Percent);
        Assert.Equal(0.0, new UpdateProgressEventArgs(UpdateStage.Verify, 10, 0, 0).Percent);
        Assert.Equal(100.0, new UpdateProgressEventArgs(UpdateStage.Commit, 1500, 1000, 0).Percent);
        var e = new UpdateProgressEventArgs(UpdateStage.Download, 250, 1000, 5.5);
        Assert.Equal(UpdateStage.Download, e.Stage);
        Assert.Equal(250, e.BytesReceived);
        Assert.Equal(1000, e.TotalBytes);
        Assert.Equal(5.5, e.BytesPerSecond);
    }

    [Fact]
    public void Failed_carries_stage_exception_and_version()
    {
        var ex = new InvalidOperationException("boom");
        var e = new UpdateFailedEventArgs(UpdateStage.Commit, ex, new Version(1, 2, 4, 0));

        Assert.Equal(UpdateStage.Commit, e.Stage);
        Assert.Same(ex, e.Exception);
        Assert.Equal(new Version(1, 2, 4, 0), e.Version);
        Assert.Null(new UpdateFailedEventArgs(UpdateStage.Check, ex, null).Version);
        Assert.Throws<ArgumentNullException>(() => new UpdateFailedEventArgs(UpdateStage.Check, null!, null));
    }
}
