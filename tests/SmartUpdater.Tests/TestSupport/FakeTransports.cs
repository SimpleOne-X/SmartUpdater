using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

internal sealed class FakeReleaseFeed : IReleaseFeed
{
    public List<string?> RequestedETags { get; } = [];

    public Func<string?, FeedResult> Responder { get; set; } = _ => FeedResult.NotModified;

    public static FakeReleaseFeed Returning(ReleaseFeedDocument document, string? etag = null)
        => new() { Responder = _ => new FeedResult(false, document, etag) };

    /// <summary>etag 相同回 304，否则回文档。</summary>
    public static FakeReleaseFeed WithETag(ReleaseFeedDocument document, string etag)
        => new() { Responder = requested => requested == etag ? FeedResult.NotModified : new FeedResult(false, document, etag) };

    public static FakeReleaseFeed Throwing(Exception exception)
        => new() { Responder = _ => throw exception };

    public Task<FeedResult> GetAsync(string? etag, CancellationToken ct)
    {
        RequestedETags.Add(etag);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Responder(etag));
    }
}

internal sealed class FakePackageDownloader : IPackageDownloader
{
    public List<(ReleaseEntry Release, string Destination)> Calls { get; } = [];

    public Func<ReleaseEntry, string, IProgress<DownloadProgress>?, CancellationToken, Task<string>> Behavior { get; set; }

    public FakePackageDownloader()
    {
        Behavior = (release, destination, progress, _) =>
        {
            progress?.Report(new DownloadProgress(release.Package.Size / 2, release.Package.Size, 1024));
            progress?.Report(new DownloadProgress(release.Package.Size, release.Package.Size, 1024));
            return Task.FromResult(destination);
        };
    }

    public static FakePackageDownloader Throwing(Exception exception)
        => new() { Behavior = (_, _, _, _) => Task.FromException<string>(exception) };

    public Task<string> DownloadAsync(ReleaseEntry release, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Calls.Add((release, destinationPath));
        ct.ThrowIfCancellationRequested();
        return Behavior(release, destinationPath, progress, ct);
    }
}

internal sealed class RecordingReporter : IUpdateReporter
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

    public List<UpdateReport> Received { get; } = [];

    public Func<UpdateReport, bool> Accept { get; set; } = _ => true;

    public Task<bool> SendAsync(UpdateReport report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool accepted = Accept(report);
        lock (_gate)
        {
            if (accepted)
            {
                Received.Add(report);
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (Received.Count >= _waiters[i].Count)
                    {
                        _waiters[i].Signal.TrySetResult();
                        _waiters.RemoveAt(i);
                    }
                }
            }
        }

        return Task.FromResult(accepted);
    }

    /// <summary>等到至少收到 count 条被接受的上报；真实时间守卫默认 10 s。</summary>
    public Task WaitForAsync(int count, TimeSpan? realTimeout = null)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (Received.Count >= count)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
        }

        return signal.Task.WaitAsync(realTimeout ?? TimeSpan.FromSeconds(10));
    }
}

internal sealed class RecordingEventSink : IUpdateEventSink
{
    public bool HasUpdateAvailableSubscribers { get; set; } = true;

    /// <summary>模拟消费方的处理器：返回要做的决定，null 表示什么也不做（Undecided）。</summary>
    public Func<UpdateAvailableEventArgs, UpdateDecision?> AvailableBehavior { get; set; } = _ => UpdateDecision.Accept;

    /// <summary>模拟消费方的 Restarting 处理器（可调用 e.WaitFor）。</summary>
    public Action<RestartingEventArgs>? RestartingBehavior { get; set; }

    public List<UpdateAvailableEventArgs> Available { get; } = [];

    public List<UpdateProgressEventArgs> Progress { get; } = [];

    public List<UpdateFailedEventArgs> Failed { get; } = [];

    public List<RestartingEventArgs> Restarting { get; } = [];

    public Task RaiseUpdateAvailableAsync(UpdateAvailableEventArgs args)
    {
        Available.Add(args);
        switch (AvailableBehavior(args))
        {
            case UpdateDecision.Accept: args.Accept(); break;
            case UpdateDecision.Postpone: args.Postpone(); break;
            case UpdateDecision.Skip: args.Skip(); break;
            default: break;
        }

        args.Seal();
        return Task.CompletedTask;
    }

    public void RaiseProgress(UpdateProgressEventArgs args) => Progress.Add(args);

    public Task RaiseFailedAsync(UpdateFailedEventArgs args)
    {
        Failed.Add(args);
        return Task.CompletedTask;
    }

    public Task RaiseRestartingAsync(RestartingEventArgs args)
    {
        Restarting.Add(args);
        RestartingBehavior?.Invoke(args);
        args.Seal();
        return Task.CompletedTask;
    }
}
