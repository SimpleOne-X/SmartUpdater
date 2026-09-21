using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>上报部分：按到达顺序原样保存客户端 POST 上来的 JSON 对象。</summary>
internal sealed partial class ControlState
{
    private readonly List<JsonNode> _reports = [];
    private readonly Lock _reportGate = new();

    public void AddReport(JsonNode report)
    {
        lock (_reportGate)
        {
            _reports.Add(report);
        }
    }

    public IReadOnlyList<JsonNode> SnapshotReports()
    {
        lock (_reportGate)
        {
            return [.. _reports];
        }
    }

    public void ClearReports()
    {
        lock (_reportGate)
        {
            _reports.Clear();
        }
    }
}
