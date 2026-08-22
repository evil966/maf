using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarrationStudio.Service;

public sealed class FlexibleNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.TryGetInt32(out var number) ? number != 0 : null,
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to Boolean.")
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteBooleanValue(value.Value);
        }
    }

    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "undefined" or "null")
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value is "1" or "yes" or "on" ? true : value is "0" or "no" or "off" ? false : null;
    }
}

public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var number) ? number : null,
            JsonTokenType.Null => null,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var parsed) ? parsed : null,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to Int32.")
        };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}
