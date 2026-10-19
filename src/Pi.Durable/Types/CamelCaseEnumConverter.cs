using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pi.Durable.Types;

public sealed class CamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected a string enum value");
        var value = reader.GetString();
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (JsonNamingPolicy.CamelCase.ConvertName(candidate.ToString()) == value) return candidate;
        }
        throw new JsonException($"Unknown {typeof(TEnum).Name} value: {value}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
