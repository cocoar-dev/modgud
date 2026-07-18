using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Modgud.Client.AspNetCore;

/// <summary>
/// Pre-request claims-transformation that flattens
/// <c>resource_access[<see cref="ModgudOptions.Audience"/>]</c> from
/// the principal's claims into flat <see cref="ClaimTypes.Role"/> and
/// <c>"permission"</c> claims so downstream gates work without per-endpoint
/// plumbing.
///
/// <para>Source of the data — preference order: the access token's own
/// embedded <c>resource_access</c> claim wins (federation v1.1 bakes it in
/// at issuance), with <c>/connect/userinfo</c> as a fallback for tokens
/// that don't carry it (see <see cref="ModgudUserInfoEnricher"/>). Both
/// paths land the claim on the identity the exact same way: JwtBearer's
/// token handler maps a JSON payload property to a claim of type
/// <c>"resource_access"</c> whose <c>Value</c> is the raw JSON text and
/// whose <c>ValueType</c> is
/// <c>Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes.Json</c>
/// (<c>"JSON"</c>); the enricher's UserInfo fallback adds a claim with the
/// same type and a raw-JSON-text value. This transformer only ever reads
/// <see cref="Claim.Value"/>, so it is indifferent to which path populated
/// the claim or to <see cref="Claim.ValueType"/> — it just needs valid JSON
/// text under the <c>"resource_access"</c> claim type. Because UserInfo
/// only ever echoes the same block the token already carries (never a wider
/// or narrower one), preferring the token claim changes nothing about what
/// ends up on the principal — it only removes a redundant HTTP round-trip
/// for tokens that already have the claim.</para>
///
/// <para>Idempotent: a second pass on the same identity does not duplicate
/// claims.</para>
///
/// <para>The IdP pre-expands bypass tiers before emission, so this lib
/// performs no <c>realm:admin</c> / <c>&lt;r&gt;:admin</c> walk —
/// <see cref="RequiresModgudPermissionFilter"/> just reads the
/// <c>"permission"</c> claims and does <c>contains(...)</c>.</para>
/// </summary>
public sealed class ModgudClaimsTransformation : IClaimsTransformation
{
    /// <summary>Claim type for permission strings (<c>"&lt;resource&gt;:&lt;action&gt;"</c>).</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>
    /// Claim type that USED to carry flattened group names.
    /// </summary>
    /// <remarks>
    /// Quarantined in federation v1 (hub boundary): the Modgud IdP never emits a
    /// <c>groups</c> block in <c>resource_access</c> — group membership is purely
    /// IdP-internal and is expanded into roles/permissions before emission. This
    /// transformer therefore never produces a claim of this type. The constant is
    /// retained for binary compatibility and will be removed in a future major
    /// version. Gate on roles/permissions instead.
    /// </remarks>
    [Obsolete("Hub boundary: the Modgud IdP never emits groups in resource_access, " +
        "so no claim of this type is ever produced. Gate on roles/permissions instead. " +
        "Retained for binary compatibility; removed in a future major version.")]
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
        // Federation v1 hub boundary: the IdP never emits a "groups" block here
        // (group membership is IdP-internal, expanded into roles/permissions before
        // emission), so there is nothing to flatten. The legacy group flattener was
        // removed; GroupClaimType is retained [Obsolete] for binary compatibility.

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
