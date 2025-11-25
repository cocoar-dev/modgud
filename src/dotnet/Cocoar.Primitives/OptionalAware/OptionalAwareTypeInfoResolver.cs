using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cocoar.Primitives.OptionalAware;

public sealed class OptionalAwareTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Properties is null)
            return typeInfo;

        foreach (var property in typeInfo.Properties)
        {
            if (IsOptionalType(property.PropertyType))
            {
                property.ShouldSerialize = static (parent, propertyValue) =>
                {
                    var shouldSerialize = propertyValue is IOptional opt && opt.HasValue;
                    return shouldSerialize;
                };
            }
        }

        return typeInfo;
    }

    private static bool IsOptionalType(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>);
}
