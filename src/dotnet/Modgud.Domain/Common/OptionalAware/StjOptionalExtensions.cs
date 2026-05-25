using System.Text.Json;

namespace Modgud.Domain.Common;

public static class StjOptionalExtensions
{
    /// <summary>
    /// Registers Optional{T} serialization support on System.Text.Json options:
    /// OptionalJsonConverterFactory + OptionalAwareTypeInfoResolver.
    /// </summary>
    public static JsonSerializerOptions AddOptionalAware(this JsonSerializerOptions options)
    {
        var hasConverter = false;
        foreach (var c in options.Converters)
        {
            if (c is OptionalJsonConverterFactory) { hasConverter = true; break; }
        }
        if (!hasConverter)
            options.Converters.Add(new OptionalJsonConverterFactory());

        options.TypeInfoResolver = new OptionalAwareTypeInfoResolver();

        return options;
    }
}
