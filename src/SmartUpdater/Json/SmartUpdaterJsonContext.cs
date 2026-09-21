using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleOneX.SmartUpdater;

/// <summary>把 <see cref="Version"/> 读写为 "1.2.3.4" 形式的字符串。</summary>
internal sealed class VersionJsonConverter : JsonConverter<Version>
{
    public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (!Version.TryParse(text, out Version? value))
        {
            throw new JsonException($"'{text}' 不是合法的版本号。");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>可空版本号转换器。JSON 里字段缺失或为 null 时返回 null。</summary>
internal sealed class NullableVersionJsonConverter : JsonConverter<Version?>
{
    public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        string? text = reader.GetString();
        if (!Version.TryParse(text, out Version? value))
        {
            throw new JsonException($"'{text}' 不是合法的版本号。");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, Version? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}

/// <summary>把枚举读写为 camelCase 字符串（AOT 安全的泛型版本）。</summary>
internal sealed class CamelCaseEnumConverter<T> : JsonStringEnumConverter<T>
    where T : struct, Enum
{
    // 协议线格式是字符串，"mode": 7 这类整数一律拒绝
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>本包全部 JSON 读写的唯一入口。AOT 要求一律走源生成上下文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    Converters = [typeof(VersionJsonConverter)])]
[JsonSerializable(typeof(ReleaseFeedDocument))]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(ReleaseEntry))]
[JsonSerializable(typeof(ClientPolicy))]
[JsonSerializable(typeof(UpdateReport))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class SmartUpdaterJsonContext : JsonSerializerContext;
