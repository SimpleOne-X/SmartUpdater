namespace SimpleOneX.SmartUpdater;

/// <summary>把日志转给消费方提供的回调（<c>Logger</c> 委托）。阶段拼进消息前缀，异常原样传递。</summary>
internal sealed class CallbackLog : IUpdateLog
{
    private readonly Action<UpdateLogLevel, string, Exception?> _callback;

    /// <summary>包住消费方的回调。</summary>
    public CallbackLog(Action<UpdateLogLevel, string, Exception?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    /// <inheritdoc />
    public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
    {
        try
        {
            _callback(level, stage is null ? message : $"[{stage}] {message}", exception);
        }
        catch (Exception)
        {
            // 此处刻意留空：抛异常的是消费方的回调，那是消费方的 bug，
            // 不能让更新因此失败；而日志本身就是这里唯一的记录通道，回调坏了没有别的地方可记。
        }
    }
}

/// <summary>把同一条日志扇出到多个 sink（例如文件日志加消费方回调）。逐个隔离：一个 sink 抛异常不影响其余 sink。</summary>
internal sealed class CompositeLog : IUpdateLog
{
    private readonly IUpdateLog[] _sinks;

    /// <summary>按给定顺序调用各 sink。</summary>
    public CompositeLog(params IUpdateLog[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = [.. sinks];
    }

    /// <inheritdoc />
    public void Write(UpdateLogLevel level, UpdateStage? stage, string message, Exception? exception = null)
    {
        foreach (IUpdateLog sink in _sinks)
        {
            try
            {
                sink.Write(level, stage, message, exception);
            }
            catch (Exception)
            {
                // 与 CallbackLog 同理：sink 违反了"日志实现不得抛异常"的契约，
                // 这是它的 bug，不能连累其余 sink，更不能让更新失败；此处没有比日志更靠后的记录通道。
            }
        }
    }
}
