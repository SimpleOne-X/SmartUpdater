namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>
/// 包住响应体：按给定速率节流，并可在写满 <paramref name="cutAfterBytes"/> 之后停止写入。
/// 只用于测试用的模拟服务器，不追求精确的令牌桶语义：数据按固定的 4096 字节分块，每块写出并 Flush 之后
/// 再等 <c>块大小 / 速率</c> 秒。
/// </summary>
/// <param name="inner">真正的响应体流。不归本流所有，Dispose 本流不会 Dispose 它。</param>
/// <param name="bytesPerSecond">&lt;= 0 表示不限速。</param>
/// <param name="cutAfterBytes">null 表示不切断；n 表示只放行前 n 字节，其余丢弃。</param>
/// <param name="delay">节流用的等待函数，默认是 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>；测试注入它来记录每次等待而不真的等。</param>
internal sealed class ThrottleStream(
    Stream inner,
    int bytesPerSecond,
    long? cutAfterBytes,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : Stream
{
    /// <summary>块大小固定，让延时粒度可预测。</summary>
    internal const int ChunkSize = 4096;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));

    private long _written;

    /// <summary>
    /// 有字节因 <c>cutAfterBytes</c> 被丢弃了。调用方据此让客户端看到响应被截断。
    /// 只有"想写的比允许的多"才算切断：响应体恰好等于 <c>cutAfterBytes</c> 时一个字节都没丢，不置位。
    /// </summary>
    public bool WasCut { get; private set; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int chunk = Math.Min(ChunkSize, buffer.Length - offset);
            if (cutAfterBytes is { } limit)
            {
                // 切断之后 _written 恒等于 limit，所以之后的每次写入都走到这里直接丢弃，不需要另外记一个"已切断"的早退。
                long remaining = limit - _written;
                if (remaining <= 0)
                {
                    WasCut = true;
                    return;
                }

                // 只写到恰好 limit 字节，不按整块切：客户端据此用 Range 续传，多一个少一个字节都会拼错。
                chunk = (int)Math.Min(chunk, remaining);
            }

            await inner.WriteAsync(buffer.Slice(offset, chunk), cancellationToken);

            // 每块之后都 Flush：否则字节会停在服务器的缓冲区里，客户端在断开之前一个字节都收不到。
            await inner.FlushAsync(cancellationToken);
            _written += chunk;
            offset += chunk;

            if (bytesPerSecond > 0)
            {
                await _delay(TimeSpan.FromSeconds((double)chunk / bytesPerSecond), cancellationToken);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
