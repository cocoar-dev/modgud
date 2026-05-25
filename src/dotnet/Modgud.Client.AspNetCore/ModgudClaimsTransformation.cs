using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Modgud.Client.AspNetCore;

/// <summary>
/// Pre-request claims-transformation that flattens
/// <c>resource_access[<see cref="ModgudOptions.Audience"/>]</c> from
/// the principal's claims into flat <see cref="ClaimTypes.Role"/>,
/// <c>"permission"</c> and <c>"group"</c> claims so downstream gates work
/// without per-endpoint plumbing.
///
/// <para>Source of the data: the JWT-bearer middleware populates
/// <c>resource_access</c> as a string-typed claim when configured with
/// <c>options.GetClaimsFromUserInfoEndpoint = true</c> (or when the token
/// itself carries it). Both shapes are tolerated: the value can be a JSON
/// object string, or already a <see cref="JsonElement"/> from a fancier
/// validation handler.</para>
///
/// <para>Idempotent: a second pass on the same identity does not duplicate
/// claims.</para>
///
/// <para>The IdP pre-expands bypass tiers before emission, so this lib
/// performs no <c>realm:admin</c> / <c>&lt;r&gt;:admin</c> walk —
/// <see cref="RequiresCocoarPermissionFilter"/> just reads the
/// <c>"permission"</c> claims and does <c>contains(...)</c>.</para>
/// </summary>
public sealed class ModgudClaimsTransformation : IClaimsTransformation
{
    /// <summary>Claim type for permission strings (<c>"&lt;resource&gt;:&lt;action&gt;"</c>).</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>Claim type for group names.</summary>
    public const string GroupClaimType = "group";

    /// <summary>The standard OIDC/Keycloak UserInfo claim that nests per-RS authz info.</summary>
    public const string ResourceAccessClaimType = "resource_access";

    private readonly ModgudOptions _options;

    public ModgudClaimsTransformation(IOptions<ModgudOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Audience))
            throw new InvalidOperationException(
                "ModgudOptions.Audience must be set to the resource server's audience " +
                "(same value as JwtBearerOptions.Audience). Configure it via AddModgudClient.");
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        var raw = identity.FindFirst(ResourceAccessClaimType)?.Value;
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult(principal);

        if (!TryParseJson(raw, out var resourceAccess) ||
            resourceAccess.ValueKind != JsonValueKind.Object)
            return Task.FromResult(principal);

        if (!resourceAccess.TryGetProperty(_options.Audience, out var audienceBlock) ||
            audienceBlock.ValueKind != JsonValueKind.Object)
            return Task.FromResult(principal);

        FlattenStringArray(identity, audienceBlock, "roles", ClaimTypes.Role);
        FlattenStringArray(identity, audienceBlock, "permissions", PermissionClaimType);
        FlattenGroupObjectArray(identity, audienceBlock);

        return Task.FromResult(principal);
    }

    /// <summary>
    /// Adds each string in <paramref name="audienceBlock"/>[<paramref name="property"/>]
    /// as a <paramref name="claimType"/> claim. Skips duplicates so a second
    /// pipeline pass doesn't bloat the identity.
    /// </summary>
    private static void FlattenStringArray(
        ClaimsIdentity identity, JsonElement audienceBlock, string property, string claimType)
    {
        if (!audienceBlock.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return;

        var existing = new HashSet<string>(
            identity.FindAll(claimType).Select(c => c.Value),
            StringComparer.Ordinal);

        foreach (var element in array.EnumerateArray())
        {
            var value = element.GetString();
            if (string.IsNullOrEmpty(value) || !existing.Add(value)) continue;
            identity.AddClaim(new Claim(claimType, value));
        }
    }

    /// <summary>
    /// Groups arrive as <c>[{ "id": "...", "name": "..." }]</c> objects.
    /// Flatten the <c>name</c> field into <c>"group"</c> claims (the id is
    /// kept on the original <c>resource_access</c> string claim if a
    /// caller needs it — flattening just one field per object is the
    /// standard pattern).
    /// </summary>
    private static void FlattenGroupObjectArray(ClaimsIdentity identity, JsonElement audienceBlock)
    {
        if (!audienceBlock.TryGetProperty("groups", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return;

        var existing = new HashSet<string>(
            identity.FindAll(GroupClaimType).Select(c => c.Value),
            StringComparer.Ordinal);

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("name", out var nameElement)) continue;
            var name = nameElement.GetString();
            if (string.IsNullOrEmpty(name) || !existing.Add(name)) continue;
            identity.AddClaim(new Claim(GroupClaimType, name));
        }
    }

    private static bool TryParseJson(string raw, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
