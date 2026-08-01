using System.Security.Claims;
using System.Text.Json;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>Claim names projected by the Modgud resource-server package.</summary>
public static class ModgudClaimTypes
{
    /// <summary>A concrete <c>&lt;resource&gt;:&lt;action&gt;</c> permission.</summary>
    public const string Permission = "permission";

    /// <summary>The per-audience authorization object emitted by Modgud.</summary>
    public const string ResourceAccess = "resource_access";
}

/// <summary>
/// Projects one scheme's configured audience block directly onto its
/// authenticated identity. This deliberately does not use
/// <c>IClaimsTransformation</c>: the audience belongs to the authentication
/// scheme that validated the token, not to global application state.
/// </summary>
internal static class ModgudClaimsProjector
{
    public static void Project(ClaimsPrincipal? principal, string audience)
    {
        if (principal?.Identity is not ClaimsIdentity identity ||
            !identity.IsAuthenticated ||
            string.IsNullOrWhiteSpace(audience))
        {
            return;
        }

        var raw = identity.FindFirst(ModgudClaimTypes.ResourceAccess)?.Value;
        if (string.IsNullOrWhiteSpace(raw) ||
            !TryParseJson(raw, out var resourceAccess) ||
            resourceAccess.ValueKind != JsonValueKind.Object ||
            !resourceAccess.TryGetProperty(audience, out var audienceBlock) ||
            audienceBlock.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        FlattenStringArray(identity, audienceBlock, "roles", ClaimTypes.Role);
        FlattenStringArray(identity, audienceBlock, "permissions", ModgudClaimTypes.Permission);
    }

    private static void FlattenStringArray(
        ClaimsIdentity identity,
        JsonElement audienceBlock,
        string property,
        string claimType)
    {
        if (!audienceBlock.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var existing = new HashSet<string>(
            identity.FindAll(claimType).Select(c => c.Value),
            StringComparer.Ordinal);

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;
            var value = element.GetString();
            if (string.IsNullOrEmpty(value) || !existing.Add(value)) continue;
            identity.AddClaim(new Claim(claimType, value));
        }
    }

    private static bool TryParseJson(string raw, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
