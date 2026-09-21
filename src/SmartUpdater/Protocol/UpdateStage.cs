using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>更新流水线的阶段。进度事件与失败上报共用，取值为名词，与流水线步骤一一对应；只放阶段，不放失败原因。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateStage>))]
public enum UpdateStage
{
    /// <summary>拉取 feed、挑选版本、灰度判定。</summary>
    Check = 0,

    /// <summary>下载升级包（含断点续传与退避重试）。</summary>
    Download = 1,

    /// <summary>校验包级 SHA-256、可选验签、逐文件 hash。</summary>
    Verify = 2,

    /// <summary>磁盘空间预检。</summary>
    DiskCheck = 3,

    /// <summary>安装目录可写性检测。</summary>
    PermissionCheck = 4,

    /// <summary>写 .sunew、journal、改名提交。</summary>
    Commit = 5,

    /// <summary>用 .suold 回滚。</summary>
    Rollback = 6,
}
