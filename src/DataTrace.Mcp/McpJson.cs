using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DataTrace.Mcp;

/// <summary>
/// 工具输出统一使用 camelCase，并把库里的 Unspecified 时间按本机时区输出为带偏移的 ISO 8601。
/// </summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new LocalDateTimeConverter());
        return options;
    }
}

public sealed class LocalDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local),
            _ => value
        };
        writer.WriteStringValue(local.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
    }
}
