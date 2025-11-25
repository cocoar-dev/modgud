using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cocoar.Primitives
{

    public class ShortGuidJsonConverter : JsonConverter<ShortGuid>
    {
        public override ShortGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is null ? ShortGuid.Empty : new ShortGuid(value);
        }

        public override void Write(Utf8JsonWriter writer, ShortGuid value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.ToString(), options);
        }
    }
}
