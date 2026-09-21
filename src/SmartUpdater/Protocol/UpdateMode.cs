using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>一次更新对使用者是否可拒绝。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<UpdateMode>))]
public enum UpdateMode
{
    /// <summary>提示用户，用户可以拒绝或跳过。</summary>
    Optional = 0,

    /// <summary>不询问，检测到即执行。</summary>
    Mandatory = 1,
}
