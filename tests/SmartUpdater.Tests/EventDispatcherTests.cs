using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class EventDispatcherTests
{
    private static UpdateFailedEventArgs Args() => new(UpdateStage.Check, new InvalidOperationException("x"), null);

    [Fact]
    public async Task Without_context_handlers_run_inline_before_the_task_completes()
    {
        var log = new RecordingLog();
        var dispatcher = new EventDispatcher(null, log);
        int calls = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int? handlerThread = null;
        EventHandler<UpdateFailedEventArgs> handler = (_, _) => { calls++; handlerThread = Environment.CurrentManagedThreadId; };

        Task task = dispatcher.InvokeAsync(handler, this, Args());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.Equal(callerThread, handlerThread);
        Assert.False(dispatcher.HasContext);
        await task;
    }

    [Fact]
    public async Task With_context_handlers_run_only_when_the_context_drains()
    {
        var ctx = new RecordingSynchronizationContext();
        var dispatcher = new EventDispatcher(ctx, new RecordingLog());
        SynchronizationContext? seen = null;
        int calls = 0;
        EventHandler<UpdateFailedEventArgs> handler = (_, _) => { calls++; seen = SynchronizationContext.Current; };

        Task task = dispatcher.InvokeAsync(handler, this, Args());

        Assert.False(task.IsCompleted);
        Assert.Equal(0, calls);
        Assert.Equal(1, ctx.PostCount);
        Assert.True(dispatcher.HasContext);

        Assert.Equal(1, ctx.RunAll());
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Same(ctx, seen);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_others_and_is_logged()
    {
        var log = new RecordingLog();
        var dispatcher = new EventDispatcher(null, log);
        var order = new List<string>();
        EventHandler<UpdateFailedEventArgs> handler = (_, _) => { order.Add("first"); throw new InvalidOperationException("handler boom"); };
        handler += (_, _) => order.Add("second");

        await dispatcher.InvokeAsync(handler, this, Args());

        Assert.Equal(["first", "second"], order);
        LogEntry error = Assert.Single(log.AtLevel(UpdateLogLevel.Error));
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("handler boom", error.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_handler_on_a_context_still_completes_the_task()
    {
        var ctx = new RecordingSynchronizationContext();
        var log = new RecordingLog();
        var dispatcher = new EventDispatcher(ctx, log);
        EventHandler<UpdateFailedEventArgs> handler = (_, _) => throw new InvalidOperationException("ui boom");

        Task task = dispatcher.InvokeAsync(handler, this, Args());
        ctx.RunAll();

        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Single(log.AtLevel(UpdateLogLevel.Error));
    }

    [Fact]
    public async Task Null_handler_completes_immediately_without_posting()
    {
        var ctx = new RecordingSynchronizationContext();
        var dispatcher = new EventDispatcher(ctx, new RecordingLog());

        Task task = dispatcher.InvokeAsync<UpdateFailedEventArgs>(null, this, Args());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, ctx.PostCount);
        await task;
    }

    [Fact]
    public void Post_does_not_wait_and_preserves_order()
    {
        var ctx = new RecordingSynchronizationContext();
        var dispatcher = new EventDispatcher(ctx, new RecordingLog());
        var seen = new List<double>();
        EventHandler<UpdateProgressEventArgs> handler = (_, e) => seen.Add(e.Percent);

        dispatcher.Post(handler, this, new UpdateProgressEventArgs(UpdateStage.Download, 10, 100, 0));
        dispatcher.Post(handler, this, new UpdateProgressEventArgs(UpdateStage.Download, 20, 100, 0));

        Assert.Empty(seen);
        Assert.Equal(2, ctx.RunAll());
        Assert.Equal([10, 20], seen);
    }

    [Fact]
    public void Post_without_context_runs_inline_and_isolates_exceptions()
    {
        var log = new RecordingLog();
        var dispatcher = new EventDispatcher(null, log);
        int calls = 0;
        EventHandler<UpdateProgressEventArgs> handler = (_, _) => throw new InvalidOperationException("p");
        handler += (_, _) => calls++;

        dispatcher.Post(handler, this, new UpdateProgressEventArgs(UpdateStage.Download, 1, 1, 0));

        Assert.Equal(1, calls);
        Assert.Single(log.AtLevel(UpdateLogLevel.Error));
    }

    [Fact]
    public async Task Sender_and_args_are_passed_through()
    {
        var dispatcher = new EventDispatcher(null, new RecordingLog());
        object? seenSender = null;
        UpdateFailedEventArgs? seenArgs = null;
        UpdateFailedEventArgs args = Args();
        EventHandler<UpdateFailedEventArgs> handler = (s, e) => { seenSender = s; seenArgs = e; };

        await dispatcher.InvokeAsync(handler, this, args);

        Assert.Same(this, seenSender);
        Assert.Same(args, seenArgs);
    }

    [Fact]
    public async Task DrainUntilAsync_supports_handlers_raised_from_another_thread()
    {
        var ctx = new RecordingSynchronizationContext();
        var dispatcher = new EventDispatcher(ctx, new RecordingLog());
        int calls = 0;
        EventHandler<UpdateFailedEventArgs> handler = (_, _) => calls++;

        Task work = Task.Run(() => dispatcher.InvokeAsync(handler, this, Args()), TestContext.Current.CancellationToken);

        await ctx.DrainUntilAsync(work);
        Assert.Equal(1, calls);
    }
}
