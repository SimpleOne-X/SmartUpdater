namespace SimpleOneX.SmartUpdater;

/// <summary>把一条上报送出去。时机、内容与补报队列由包负责。</summary>
public interface IUpdateReporter
{
    /// <summary>
    /// 包保证只在有待上报记录时调用。
    /// 返回 false 或抛异常 → 包把这条留在队列里，下次再试。
    /// </summary>
    Task<bool> SendAsync(UpdateReport report, CancellationToken ct);
}
