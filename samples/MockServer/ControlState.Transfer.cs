namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>传输部分：按精确路径记录限速与中途断流。与故障部分同构（字典 + 锁 + 路径规范化，大小写不敏感）。</summary>
internal sealed partial class ControlState
{
    private readonly Dictionary<string, TransferEntry> _transfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _transferGate = new();

    /// <summary>登记一条传输设置；同一路径再次登记会整条替换，而不是与旧设置合并。</summary>
    public void SetTransfer(TransferRequest transfer)
    {
        lock (_transferGate)
        {
            _transfers[NormalizePath(transfer.Path)] = new TransferEntry(transfer.BytesPerSecond ?? 0, transfer.CutAfterBytes);
        }
    }

    /// <summary>该路径有传输设置时返回 true。<paramref name="bytesPerSecond"/> 为 0 表示不限速。</summary>
    public bool TryGetTransfer(string path, out int bytesPerSecond, out long? cutAfterBytes)
    {
        lock (_transferGate)
        {
            if (_transfers.TryGetValue(NormalizePath(path), out TransferEntry entry))
            {
                bytesPerSecond = entry.BytesPerSecond;
                cutAfterBytes = entry.CutAfterBytes;
                return true;
            }
        }

        bytesPerSecond = 0;
        cutAfterBytes = null;
        return false;
    }

    private readonly record struct TransferEntry(int BytesPerSecond, long? CutAfterBytes);
}
