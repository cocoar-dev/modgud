using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modgud.Domain.Realms;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// Server-side authority for composition definitions and page publication.
/// The browser package provides the authoring UX; anonymous IDP requests only
/// ever receive the compiled, repository-free document produced here.
/// </summary>
public static class PageCompositionDocumentService
{
    private const int MaxSchemaBytes = 256 * 1024;
    private const int MaxNodes = 500;
    private const int MaxDepth = 30;

    private static readonly HashSet<string> ElementTypes =
    [
        "stack", "repeat", "card", "section", "divider", "spacer",
        "heading", "paragraph", "note", "feedback", "text-input",
        "password-input", "otp-input", "checkbox", "button", "link", "image",
        "visual-markup", "modgud-brand-header",
    ];

    public static bool ValidateCompositionRoot(
        string rootJson,
        out string error)
    {
        if (Encoding.UTF8.GetByteCount(rootJson) > MaxSchemaBytes)
        {
            error = $"Composition root exceeds {MaxSchemaBytes} bytes.";
            return false;
        }

        try
        {
            var root = JsonNode.Parse(rootJson) as JsonObject;
            if (root is null)
            {
                error = "Composition root must be one element object.";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var nodeCount = 0;
            var validationError = string.Empty;

            bool Walk(JsonObject node, int depth)
            {
                if (depth > MaxDepth)
                {
                    validationError = $"Composition exceeds maximum depth {MaxDepth}.";
                    return false;
                }
                if (++nodeCount > MaxNodes)
                {
                    validationError = $"Composition exceeds maximum node count {MaxNodes}.";
                    return false;
                }
                if (!TryString(node, "id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    validationError = "Every composition node needs a non-empty id.";
                    return false;
                }
                if (!ids.Add(id))
                {
                    validationError = $"Duplicate composition node id '{id}'.";
                    return false;
                }
                if (!TryString(node, "type", out var type) || !ElementTypes.Contains(type))
                {
                    validationError = $"Composition node '{id}' uses a disallowed type.";
                    return false;
                }
                if (node.ContainsKey("stateCode"))
                {
                    validationError = $"Composition node '{id}' cannot define Page State code.";
                    return false;
                }
                if (node.TryGetPropertyValue("composition", out var reference)
                    && !TryReference(reference, out _, out _))
                {
                    validationError = $"Composition node '{id}' contains an invalid composition reference.";
                    return false;
                }
                if (!node.TryGetPropertyValue("children", out var children) || children is null)
                    return true;
                if (children is not JsonArray childArray)
                {
                    validationError = $"Composition node '{id}' children must be an array.";
                    return false;
                }
                foreach (var child in childArray)
                {
                    if (child is not JsonObject childObject)
                    {
                        validationError = "Every composition node must be an object.";
                        return false;
                    }
                    if (!Walk(childObject, depth + 1)) return false;
                }
                return true;
            }

            if (!Walk(root, 0))
            {
                error = validationError;
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Composition root is not valid JSON: {ex.Message}";
            return false;
        }
    }

    public static bool ValidateReferences(
        string documentJson,
        IReadOnlyCollection<PageComposition> compositions,
        out string error)
    {
        try
        {
            var document = JsonNode.Parse(documentJson);
            if (document is null)
            {
                error = "Page document is empty.";
                return false;
            }

            var byId = compositions.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var validationError = string.Empty;

            bool Visit(string id, string version, List<string> stack)
            {
                var key = $"{id}\0{version}";
                var cycleIndex = stack.IndexOf(key);
                if (cycleIndex >= 0)
                {
                    var cycle = stack.Skip(cycleIndex).Append(key)
                        .Select(item => item.Replace('\0', '@'));
                    validationError = $"Composition cycle detected: {string.Join(" → ", cycle)}";
                    return false;
                }
                if (visited.Contains(key)) return true;
                if (!byId.TryGetValue(id, out var composition)
                    || !int.TryParse(version, out var versionNumber)
                    || composition.Versions.FirstOrDefault(item => item.Number == versionNumber) is not { } definition)
                {
                    validationError = $"Composition {id}@{version} is missing.";
                    return false;
                }

                var root = JsonNode.Parse(definition.Root);
                if (root is null)
                {
                    validationError = $"Composition {id}@{version} has an invalid root.";
                    return false;
                }
                var nextStack = stack.Append(key).ToList();
                var nestedReferences = CollectReferences(root, out var referenceError);
                if (!string.IsNullOrEmpty(referenceError))
                {
                    validationError = referenceError;
                    return false;
                }
                foreach (var (nestedId, nestedVersion) in nestedReferences)
                {
                    if (!Visit(nestedId, nestedVersion, nextStack))
                        return false;
                }
                visited.Add(key);
                return true;
            }

            var documentReferences = CollectReferences(document, out var documentReferenceError);
            if (!string.IsNullOrEmpty(documentReferenceError))
            {
                error = documentReferenceError;
                return false;
            }
            foreach (var (id, version) in documentReferences)
            {
                if (!Visit(id, version, []))
                {
                    error = validationError;
                    return false;
                }
            }
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Schema is not valid JSON: {ex.Message}";
            return false;
        }
    }

    public static bool ValidateAndCompilePage(
        string slug,
        string authoringSchema,
        IReadOnlyCollection<PageComposition> compositions,
        out string runtimeSchema,
        out string error)
    {
        runtimeSchema = string.Empty;
        if (!ValidateReferences(authoringSchema, compositions, out error)) return false;

        try
        {
            var runtime = JsonNode.Parse(authoringSchema);
            if (runtime is null)
            {
                error = "Page document is empty.";
                return false;
            }
            StripCompositionMetadata(runtime);
            runtimeSchema = runtime.ToJsonString();
            return PageDocumentValidator.Validate(slug, runtimeSchema, out error);
        }
        catch (JsonException ex)
        {
            error = $"Schema is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static IEnumerable<(string Id, string Version)> CollectReferences(
        JsonNode node,
        out string error)
    {
        var result = new List<(string, string)>();
        var collectionError = string.Empty;
        void Walk(JsonNode current)
        {
            if (!string.IsNullOrEmpty(collectionError) || current is not JsonObject obj) return;
            if (obj.TryGetPropertyValue("composition", out var reference))
            {
                if (!TryReference(reference, out var id, out var version))
                {
                    var nodeId = TryString(obj, "id", out var value) ? value : "unknown";
                    collectionError = $"Element {nodeId} contains an invalid composition reference. Both id and version are required.";
                    return;
                }
                result.Add((id, version));
            }
            if (obj["children"] is not JsonArray children) return;
            foreach (var child in children)
                if (child is not null) Walk(child);
        }
        Walk(node);
        error = collectionError;
        return result;
    }

    private static void StripCompositionMetadata(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        obj.Remove("composition");
        obj.Remove("compositionOrigins");
        if (obj["children"] is not JsonArray children) return;
        foreach (var child in children)
            if (child is not null) StripCompositionMetadata(child);
    }

    private static bool TryReference(JsonNode? node, out string id, out string version)
    {
        id = string.Empty;
        version = string.Empty;
        return node is JsonObject reference
            && TryString(reference, "id", out id)
            && !string.IsNullOrWhiteSpace(id)
            && TryString(reference, "version", out version)
            && !string.IsNullOrWhiteSpace(version);
    }

    private static bool TryString(JsonObject obj, string property, out string value)
    {
        value = string.Empty;
        return obj[property] is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out value!);
    }
}
