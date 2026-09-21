namespace SimpleOneX.SmartUpdater;

/// <summary>UpdateClient 对内部组件暴露的事件出口（EventDispatcher 实现；测试用 RecordingEventSink）。</summary>
internal interface IUpdateEventSink
{
    /// <summary>是否有 UpdateAvailable 订阅者。</summary>
    bool HasUpdateAvailableSubscribers { get; }

    /// <summary>触发 UpdateAvailable；返回时 args 已 Seal。</summary>
    Task RaiseUpdateAvailableAsync(UpdateAvailableEventArgs args);

    /// <summary>触发进度事件；不等待。</summary>
    void RaiseProgress(UpdateProgressEventArgs args);

    /// <summary>触发失败事件。</summary>
    Task RaiseFailedAsync(UpdateFailedEventArgs args);

    /// <summary>触发 Restarting；返回时 args 已 Seal。</summary>
    Task RaiseRestartingAsync(RestartingEventArgs args);
}
