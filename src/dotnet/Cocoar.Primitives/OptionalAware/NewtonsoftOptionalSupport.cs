using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Cocoar.Primitives.OptionalAware;

/// <summary>
/// Newtonsoft.Json converter + contract resolver helpers for Optional{T}.
/// Add <see cref="OptionalJsonConverter"/> to JsonSerializerSettings.Converters and
/// set ContractResolver = new OptionalAwareContractResolver() (or wrap your existing one).
/// </summary>
public sealed class OptionalJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(Optional<>);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        // Handle null token explicitly: if property was present with null -> HasValue=true Value=null
        if (reader.TokenType == JsonToken.Null)
        {
            var instance = Activator.CreateInstance(objectType, (object?)null);
            return instance!; // Optional<T>(null)
        }

        var innerType = objectType.GetGenericArguments()[0];
        var innerValue = serializer.Deserialize(reader, innerType); // can be null
        return Activator.CreateInstance(objectType, innerValue);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        // We expect HasValue==true due to ShouldSerialize gate; still guard.
        var hasValueProp = value.GetType().GetProperty(nameof(IOptional.HasValue));
        var hasValue = hasValueProp is not null && (bool)hasValueProp.GetValue(value)!;
        if (!hasValue)
        {
            writer.WriteNull();
            return;
        }

        var valueField = value.GetType().GetField("Value");
        var inner = valueField?.GetValue(value);
        serializer.Serialize(writer, inner);
    }
}

/// <summary>
/// Contract resolver that suppresses Optional{T} properties when HasValue == false.
/// </summary>
public class OptionalAwareContractResolver : DefaultContractResolver
{
    private readonly IContractResolver? _inner;

    public OptionalAwareContractResolver() { }
    public OptionalAwareContractResolver(IContractResolver inner)
    {
        _inner = inner;
    }

    protected override JsonContract CreateContract(Type objectType)
    {
        if (_inner is not null)
            return _inner.ResolveContract(objectType);
        return base.CreateContract(objectType);
    }

    protected override JsonProperty CreateProperty(System.Reflection.MemberInfo member, MemberSerialization memberSerialization)
    {
        var prop = base.CreateProperty(member, memberSerialization);

        if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(Optional<>))
        {
            // Skip serialization unless HasValue == true
            prop.ShouldSerialize = instance =>
            {
                var optObj = prop.ValueProvider.GetValue(instance);
                if (optObj is IOptional opt) return opt.HasValue;
                return false;
            };

            // Ensure our converter is used (respect existing one if set explicitly)
            prop.Converter ??= new OptionalJsonConverter();
        }

        return prop;
    }
}

public static class NewtonsoftOptionalExtensions
{
    /// <summary>
    /// Helper to quickly wire Optional{T} support on JsonSerializerSettings.
    /// </summary>
    public static JsonSerializerSettings AddOptionalAware(this JsonSerializerSettings settings, IContractResolver? existingResolver = null)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        // Insert converter if missing
        var hasConverter = false;
        foreach (var c in settings.Converters)
        {
            if (c is OptionalJsonConverter) { hasConverter = true; break; }
        }
        if (!hasConverter)
            settings.Converters.Add(new OptionalJsonConverter());

        settings.ContractResolver = existingResolver is null
            ? new OptionalAwareContractResolver()
            : new OptionalAwareContractResolver(existingResolver);

        return settings;
    }
}
