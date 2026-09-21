using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => "SmartUpdaterTests." + Guid.NewGuid().ToString("N");

    private static StartupResult Result(string appId = "TestApp-00000000")
        => new(justUpdated: false, fromVersion: null, toVersion: null, currentVersion: new Version(1, 0, 0, 0), appId: appId, isUpdateEnabled: true, updateDisabledReason: null, isRollbackApplied: false);

    [Fact]
    public void First_acquire_succeeds_second_returns_null_until_disposed()
    {
        string name = UniqueName();

        using (SingleInstanceHandle? first = SingleInstanceHandle.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Equal(name, first.Name);
            Assert.Null(SingleInstanceHandle.TryAcquire(name));
        }

        using SingleInstanceHandle? again = SingleInstanceHandle.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void StartupResult_composes_the_mutex_name_from_name_and_app_id()
    {
        string name = UniqueName();

        using SingleInstanceHandle? handle = Result("App-abc12345").TryAcquireSingleInstance(name);

        Assert.NotNull(handle);
        Assert.Equal($"{name}.App-abc12345", handle.Name);
        Assert.Null(SingleInstanceHandle.TryAcquire($"{name}.App-abc12345"));
        using SingleInstanceHandle? otherApp = Result("App-ffffffff").TryAcquireSingleInstance(name);
        Assert.NotNull(otherApp);      // 不同 AppId 互不干扰
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Global\MyApp")]
    public void Invalid_instance_names_are_rejected(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Result().TryAcquireSingleInstance(name));
    }

    [Fact]
    public async Task Activating_an_existing_instance_raises_ActivationRequested()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("命名事件的 TryOpenExisting 是 Windows 专用"); return; }
        string name = UniqueName();
        using SingleInstanceHandle holder = SingleInstanceHandle.TryAcquire(name)!;
        var raised = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.ActivationRequested += (sender, _) => raised.TrySetResult(sender);

        bool signalled = SmartUpdaterApp.ActivateInstance(name);

        Assert.True(signalled);
        Assert.Same(holder, await raised.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Activation_can_be_requested_more_than_once()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows 专用"); return; }
        string name = UniqueName();
        using SingleInstanceHandle holder = SingleInstanceHandle.TryAcquire(name)!;
        int count = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.ActivationRequested += (_, _) =>
        {
            int n = Interlocked.Increment(ref count);
            (n == 1 ? first : second).TrySetResult();
        };

        // AutoReset 事件会合并"上一次还没被等待线程取走"的信号，所以第二次激活要等第一次送达后再发。
        Assert.True(SmartUpdaterApp.ActivateInstance(name));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(SmartUpdaterApp.ActivateInstance(name));

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Activating_a_missing_instance_returns_false()
    {
        Assert.False(SmartUpdaterApp.ActivateInstance(UniqueName()));
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_the_holder()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows 专用"); return; }
        string name = UniqueName();
        using SingleInstanceHandle holder = SingleInstanceHandle.TryAcquire(name)!;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.ActivationRequested += (_, _) => throw new InvalidOperationException("consumer bug");
        holder.ActivationRequested += (_, _) => second.TrySetResult();

        Assert.True(SmartUpdaterApp.ActivateInstance(name));

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Dispose_is_idempotent_and_releases_the_name()
    {
        string name = UniqueName();
        SingleInstanceHandle holder = SingleInstanceHandle.TryAcquire(name)!;

        holder.Dispose();
        holder.Dispose();

        using SingleInstanceHandle? again = SingleInstanceHandle.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Rejected_acquire_is_remembered_for_ActivateExistingInstance()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows 专用"); return; }
        string name = UniqueName();
        using SingleInstanceHandle? holder = Result("App-abc12345").TryAcquireSingleInstance(name);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder!.ActivationRequested += (_, _) => raised.TrySetResult();

        Assert.Null(Result("App-abc12345").TryAcquireSingleInstance(name));

        Assert.True(SmartUpdaterApp.ActivateExistingInstance());
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
