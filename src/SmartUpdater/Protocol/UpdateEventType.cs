using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>上报事件类型。一个端点覆盖全部上行场景，用它区分用途。线格式为成员名原样（"Updated"）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateEventType>))]
public enum UpdateEventType
{
    /// <summary>更新成功，新版本首次启动时。</summary>
    Updated = 0,

    /// <summary>任一阶段失败。</summary>
    Failed = 1,

    /// <summary>周期性心跳，用于版本分布与在线状态。</summary>
    Heartbeat = 2,
}
