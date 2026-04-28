using System.Reflection;

namespace BuildingBlocks.Helper;

public static class StaticConstantsExtractor
{
    // Retrieves all constant values from a given type, including its nested types,
    // organizing them with a fully qualified namespace.
    public static Dictionary<string, List<string>> GetAllConstantsWithNamespace(Type type, string parentNamespace = "")
    {
        Dictionary<string, List<string>> constants = new Dictionary<string, List<string>>();

        // Process all public static classes recursively
        foreach (Type nestedType in type.GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            string currentNamespace = string.IsNullOrEmpty(parentNamespace) ? nestedType.Name : $"{parentNamespace}.{nestedType.Name}";

            constants[currentNamespace] = new List<string>();

            // Fetch all constant fields from the nested types
            foreach (FieldInfo field in nestedType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.IsLiteral && !field.IsInitOnly)
                {
                    var value = field.GetValue(null)?.ToString();
                    if (value != null)
                    {
                        constants[currentNamespace].Add(value);
                    }
                }
            }

            // Recursively process nested types
            var nestedConstants = GetAllConstantsWithNamespace(nestedType, currentNamespace);
            foreach (var item in nestedConstants)
            {
                constants[item.Key] = item.Value;
            }
        }

        return constants;
    }
}
