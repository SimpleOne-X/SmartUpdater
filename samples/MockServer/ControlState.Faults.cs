namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>故障部分：按精确路径记录要注入的 HTTP 故障，并数每条故障已被命中的次数。</summary>
internal sealed partial class ControlState
{
    private readonly Dictionary<string, FaultEntry> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _faultGate = new();

    /// <summary>登记一条故障；同一路径再次登记会整条替换，命中计数随之归零。</summary>
    public void SetFault(FaultRequest fault)
    {
        lock (_faultGate)
        {
            _faults[NormalizePath(fault.Path)] =
                new FaultEntry(fault.Status, fault.RetryAfterSeconds, fault.FailCount, Seen: 0);
        }
    }

    /// <summary>命中且本次应当失败时返回 true，并把计数器推进一格。</summary>
    public bool TryTakeFault(string path, out int status, out int? retryAfterSeconds)
    {
        lock (_faultGate)
        {
            string key = NormalizePath(path);
            if (!_faults.TryGetValue(key, out FaultEntry entry))
            {
                status = 0;
                retryAfterSeconds = null;
                return false;
            }

            bool shouldFail = entry.FailCount is null || entry.Seen < entry.FailCount;
            _faults[key] = entry with { Seen = entry.Seen + 1 };

            status = entry.Status;
            retryAfterSeconds = entry.RetryAfterSeconds;
            return shouldFail;
        }
    }

    public void ClearFaults()
    {
        lock (_faultGate)
        {
            _faults.Clear();
        }
    }

    internal static string NormalizePath(string path)
        => string.IsNullOrEmpty(path) ? "/" : path.StartsWith('/') ? path : "/" + path;

    private readonly record struct FaultEntry(int Status, int? RetryAfterSeconds, int? FailCount, int Seen);
}
