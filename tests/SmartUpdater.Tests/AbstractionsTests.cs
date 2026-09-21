using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class AbstractionsTests
{
    [Fact]
    public void FeedResult_NotModified_is_a_shared_instance_with_no_document()
    {
        FeedResult a = FeedResult.NotModified;
        FeedResult b = FeedResult.NotModified;

        Assert.Same(a, b);
        Assert.True(a.IsNotModified);
        Assert.Null(a.Document);
        Assert.Null(a.ETag);
    }

    [Fact]
    public void FeedResult_is_a_positional_record()
    {
        var doc = FeedFixtures.Feed(FeedFixtures.Release("1.0"));
        var r = new FeedResult(false, doc, "\"abc\"");

        Assert.False(r.IsNotModified);
        Assert.Same(doc, r.Document);
        Assert.Equal("\"abc\"", r.ETag);
        Assert.Equal(new FeedResult(false, doc, "\"abc\""), r);
    }

    [Theory]
    [InlineData(0, 1000, 0.0)]
    [InlineData(500, 1000, 50.0)]
    [InlineData(1000, 1000, 100.0)]
    [InlineData(1200, 1000, 100.0)]
    [InlineData(10, 0, 0.0)]
    [InlineData(10, -1, 0.0)]
    public void DownloadProgress_percent_is_clamped(long received, long total, double expected)
    {
        Assert.Equal(expected, new DownloadProgress(received, total, 1.0).Percent);
    }

    [Fact]
    public void UpdateEnvironment_defaults_describe_the_current_process()
    {
        var env = new UpdateEnvironment();

        Assert.Equal(AppContext.BaseDirectory, env.InstallDirectory);
        Assert.Equal(Environment.ProcessId, env.ProcessId);
        Assert.Equal(Environment.ProcessPath, env.ProcessPath);
        Assert.Equal(Environment.MachineName, env.MachineName);
        Assert.Same(TimeProvider.System, env.TimeProvider);
        Assert.Same(ProcessLauncher.Instance, env.ProcessLauncher);
        Assert.Same(ProcessWaiter.Instance, env.ProcessWaiter);
        Assert.Same(PhysicalFileOperations.Instance, env.FileOperations);
        Assert.NotEmpty(env.CommandLineArguments);
        Assert.False(string.IsNullOrWhiteSpace(env.EntryAssemblyName));
    }

    [Fact]
    public void FakeUpdateEngine_LoadState_returns_a_copy_and_SaveState_writes_back()
    {
        // 假引擎自身的守护：依赖它的测试要求"必须重新加载才能看到别人写的字段"这一语义
        var engine = new FakeUpdateEngine();
        UpdateState loaded = engine.LoadState();
        loaded.FeedETag = "\"x\"";

        Assert.Null(engine.State.FeedETag);
        engine.SaveState(loaded);
        Assert.Equal("\"x\"", engine.State.FeedETag);
        Assert.Equal(1, engine.SaveCount);
    }
}
