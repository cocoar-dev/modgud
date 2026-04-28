using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeToDo.Domain.Common;

/// <summary>
/// Converts Optional{T} to/from JSON by serializing only the inner value.
/// During deserialization, sets HasValue = true if property is present in JSON.
/// </summary>
public class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var innerType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(innerType);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }

    private class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<T>(ref reader, options);
            return new Optional<T>(value);
        }

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.HasValue ? value.Value : default, options);
        }
    }
}
