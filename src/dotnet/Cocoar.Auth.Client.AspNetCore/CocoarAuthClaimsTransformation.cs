using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Flattens Cocoar.Auth's Keycloak-style <c>resource_access</c> claim into
/// per-role <see cref="ClaimTypes.Role"/> claims so ASP.NET Core's
/// <c>[Authorize(Roles="…")]</c> works without per-endpoint plumbing.
///
/// <para>Looks up <c>resource_access[options.AppSlug].roles</c> in the
/// principal's claims and adds each role string as a <c>ClaimTypes.Role</c>
/// claim on the same identity. Idempotent: repeated runs do not duplicate
/// claims.</para>
///
/// <para>Also flattens the <c>groups</c> array into a custom
/// <c>"group"</c> claim type, kept symmetric with the role flattening so
/// downstream policy code can read group memberships uniformly.</para>
/// </summary>
public sealed class CocoarAuthClaimsTransformation : IClaimsTransformation
{
    private readonly CocoarAuthOptions _options;

    public CocoarAuthClaimsTransformation(IOptions<CocoarAuthOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.AppSlug))
            throw new InvalidOperationException(
                "CocoarAuthOptions.AppSlug must be set to the resource server's app slug " +
                "(e.g. \"timetodo\"). Configure it via AddCocoarAuthClaimsTransformation.");
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // ClaimsTransformation runs on every request — guard against the
        // un-authenticated case (anonymous endpoints) and against runs that
        // already produced the flat claims.
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        FlattenResourceAccessRoles(identity);
        FlattenGroups(identity);

        return Task.FromResult(principal);
    }

    private void FlattenResourceAccessRoles(ClaimsIdentity identity)
    {
        var raw = identity.FindFirst(_options.ResourceAccessClaimName)?.Value;
        if (string.IsNullOrEmpty(raw)) return;

        // The JWT-bearer middleware surfaces nested objects either as JSON
        // strings or as already-parsed JsonElement structures depending on
        // the IdentityModel version. Parse defensively.
        if (!TryParseJson(raw, out var resourceAccess) ||
            resourceAccess.ValueKind != JsonValueKind.Object)
            return;

        if (!resourceAccess.TryGetProperty(_options.AppSlug, out var appBlock) ||
            appBlock.ValueKind != JsonValueKind.Object)
            return;

        if (!appBlock.TryGetProperty("roles", out var rolesArray) ||
            rolesArray.ValueKind != JsonValueKind.Array)
            return;

        // Existing role claims on the identity (e.g. from a prior pass or
        // from another transformation) — skip duplicates.
        var existing = new HashSet<string>(
            identity.FindAll(ClaimTypes.Role).Select(c => c.Value),
            StringComparer.Ordinal);

        foreach (var element in rolesArray.EnumerateArray())
        {
            var role = element.GetString();
            if (string.IsNullOrEmpty(role) || !existing.Add(role)) continue;
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }

    private void FlattenGroups(ClaimsIdentity identity)
    {
        var raw = identity.FindFirst(_options.GroupsClaimName)?.Value;
        if (string.IsNullOrEmpty(raw)) return;

        if (!TryParseJson(raw, out var groups) || groups.ValueKind != JsonValueKind.Array)
            return;

        var existing = new HashSet<string>(
            identity.FindAll("group").Select(c => c.Value),
            StringComparer.Ordinal);

        foreach (var element in groups.EnumerateArray())
        {
            var group = element.GetString();
            if (string.IsNullOrEmpty(group) || !existing.Add(group)) continue;
            identity.AddClaim(new Claim("group", group));
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
