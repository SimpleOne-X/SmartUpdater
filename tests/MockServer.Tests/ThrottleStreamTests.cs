using System.Globalization;

namespace SimpleOneX.SmartUpdater.MockServer.Tests;

/// <summary>
/// ThrottleStream 的单元测试：不起服务器、不真的等。节流用的等待函数由测试注入并记录，
/// 所以每一条断言都是确定的，与机器负载无关（HTTP 层的限速 / 切断另有 TransferTests）。
/// </summary>
public sealed class ThrottleStreamTests
{
    /// <summary>按发生顺序记下 Write / Flush / Delay，并把写入的字节攒起来。</summary>
    private sealed class Recorder : Stream
    {
        public List<string> Events { get; } = [];

        public MemoryStream Written { get; } = new();

        public bool Disposed { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Events.Add($"W{buffer.Length}");
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Events.Add("F");
            return Task.CompletedTask;
        }

        public override void Flush() => Events.Add("f");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    /// <summary>造一个把每次等待记进 <paramref name="recorder"/> 而不真的等的 ThrottleStream。</summary>
    private static ThrottleStream Create(Recorder recorder, int bytesPerSecond, long? cutAfterBytes, List<CancellationToken>? tokens = null)
        => new(recorder, bytesPerSecond, cutAfterBytes, (span, token) =>
        {
            recorder.Events.Add("D" + span.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
            tokens?.Add(token);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Data_is_split_into_4096_byte_chunks_and_each_chunk_is_flushed()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: null);
        byte[] data = Bytes(10_000);

        await stream.WriteAsync(data, TestContext.Current.CancellationToken);

        Assert.Equal(["W4096", "F", "W4096", "F", "W1808", "F"], recorder.Events);
        Assert.Equal(data, recorder.Written.ToArray());
        Assert.False(stream.WasCut);
    }

    [Fact]
    public async Task Throttle_waits_chunk_size_over_rate_after_every_chunk()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 8192, cutAfterBytes: null);

        await stream.WriteAsync(Bytes(10_240), TestContext.Current.CancellationToken);

        // 4096/8192 = 0.5 秒，最后一块 2048/8192 = 0.25 秒；等待在该块写出并 Flush 之后。
        Assert.Equal(["W4096", "F", "D500", "W4096", "F", "D500", "W2048", "F", "D250"], recorder.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_rate_that_is_not_positive_means_no_throttling(int bytesPerSecond)
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond, cutAfterBytes: null);

        await stream.WriteAsync(Bytes(10_000), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(recorder.Events, e => e.StartsWith('D'));
    }

    [Fact]
    public async Task Cut_writes_exactly_the_limit_and_drops_the_rest()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: 5000);
        byte[] data = Bytes(10_000);

        await stream.WriteAsync(data, TestContext.Current.CancellationToken);

        // 第二块只写 904 字节，恰好凑满 5000，而不是整块 4096。
        Assert.Equal(["W4096", "F", "W904", "F"], recorder.Events);
        Assert.Equal(data[..5000], recorder.Written.ToArray());
        Assert.True(stream.WasCut);
        Assert.Equal(5000, stream.Position);
    }

    [Fact]
    public async Task Cut_that_spans_several_writes_still_delivers_exactly_the_limit()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: 7000);
        byte[] data = Bytes(12_000);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await stream.WriteAsync(data.AsMemory(0, 3000), cancellationToken);
        await stream.WriteAsync(data.AsMemory(3000, 3000), cancellationToken);
        Assert.False(stream.WasCut);

        await stream.WriteAsync(data.AsMemory(6000, 3000), cancellationToken);
        Assert.True(stream.WasCut);

        await stream.WriteAsync(data.AsMemory(9000, 3000), cancellationToken);

        Assert.Equal(data[..7000], recorder.Written.ToArray());
    }

    [Fact]
    public async Task A_body_exactly_as_long_as_the_limit_is_not_a_cut()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: 5000);
        byte[] data = Bytes(5000);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await stream.WriteAsync(data, cancellationToken);

        // 一个字节都没丢，所以不算切断；要再多写一个字节才算。
        Assert.False(stream.WasCut);
        Assert.Equal(data, recorder.Written.ToArray());

        await stream.WriteAsync(new byte[] { 1 }, cancellationToken);

        Assert.True(stream.WasCut);
        Assert.Equal(5000, recorder.Written.Length);
    }

    [Fact]
    public async Task Bytes_dropped_by_the_cut_are_not_throttled()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 8192, cutAfterBytes: 6144);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await stream.WriteAsync(Bytes(10_000), cancellationToken);
        await stream.WriteAsync(Bytes(10_000), cancellationToken);

        // 只有真正写出去的两块（4096 与 2048）各等了一次；被丢弃的字节不该再耗时间。
        Assert.Equal(["W4096", "F", "D500", "W2048", "F", "D250"], recorder.Events);
    }

    [Fact]
    public void The_synchronous_write_takes_the_same_path()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: 5000);
        byte[] data = Bytes(6000);

        stream.Write(data, 0, data.Length);

        Assert.Equal(data[..5000], recorder.Written.ToArray());
        Assert.True(stream.WasCut);
    }

    [Fact]
    public async Task The_write_cancellation_token_reaches_the_delay()
    {
        var recorder = new Recorder();
        var tokens = new List<CancellationToken>();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 8192, cutAfterBytes: null, tokens);
        using var source = new CancellationTokenSource();

        await stream.WriteAsync(Bytes(4096), source.Token);

        Assert.Equal(source.Token, Assert.Single(tokens));
    }

    [Fact]
    public async Task A_cancelled_delay_stops_the_write()
    {
        var recorder = new Recorder();
        var stream = new ThrottleStream(recorder, 8192, null, (_, token) => Task.FromCanceled(token));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.WriteAsync(Bytes(10_000), source.Token));

        // 第一块之后就停了，没有继续写第二块。
        Assert.Equal(4096, recorder.Written.Length);
    }

    [Fact]
    public async Task Flush_is_forwarded_and_disposing_does_not_dispose_the_inner_stream()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: null);

        await stream.FlushAsync(TestContext.Current.CancellationToken);
        stream.Flush();
        await stream.DisposeAsync();

        Assert.Equal(["F", "f"], recorder.Events);
        Assert.False(recorder.Disposed);
    }

    [Fact]
    public void The_stream_is_write_only_and_position_counts_written_bytes()
    {
        var recorder = new Recorder();
        ThrottleStream stream = Create(recorder, bytesPerSecond: 0, cutAfterBytes: null);

        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }
}
