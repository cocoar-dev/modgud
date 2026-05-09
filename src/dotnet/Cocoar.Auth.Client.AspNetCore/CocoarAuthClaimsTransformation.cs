using System.Net.Http.Headers;
using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore.Distribution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Pre-request claims-transformation that calls the Cocoar.Auth
/// distribution API and projects the response onto the principal:
/// <list type="bullet">
///   <item><c>roles[]</c> → individual <see cref="ClaimTypes.Role"/>
///   claims so <c>[Authorize(Roles="...")]</c> works natively.</item>
///   <item><c>permissions[]</c> → individual <c>"permission"</c> claims
///   the <c>RequiresCocoarPermission</c> filter reads (and the same
///   <c>PermissionEvaluator</c> the IdP uses).</item>
///   <item><c>groups[]</c> → individual <c>"group"</c> claims for
///   group-scoped row-level checks downstream.</item>
/// </list>
///
/// <para>Idempotent: a second pass on the same identity does not
/// duplicate claims (already-present values are skipped). Anonymous
/// requests are passed through untouched.</para>
///
/// <para>Failures of the distribution call are logged and swallowed —
/// the request continues with whatever claims the bearer token already
/// carried. The endpoint filter / [Authorize] gate then decides whether
/// that's enough; if not, the user gets a 403 rather than a 500. This
/// matches the security-positive default: a failed authz lookup must
/// not implicitly grant.</para>
/// </summary>
public sealed class CocoarAuthClaimsTransformation : IClaimsTransformation
{
    /// <summary>
    /// Marker the transformation sets on the identity after a successful
    /// pass so a per-pipeline second invocation (ASP.NET Core triggers
    /// IClaimsTransformation a few times across the request) doesn't
    /// re-call the distribution API.
    /// </summary>
    internal const string TransformedMarkerClaim = "cocoar-auth.transformed";

    /// <summary>Claim type for permission strings ("&lt;resource&gt;:&lt;action&gt;").</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>Claim type for group names.</summary>
    public const string GroupClaimType = "group";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDistributionClient _distributionClient;
    private readonly PermissionsCache _cache;
    private readonly CocoarAuthOptions _options;
    private readonly ILogger<CocoarAuthClaimsTransformation> _logger;

    public CocoarAuthClaimsTransformation(
        IHttpContextAccessor httpContextAccessor,
        IDistributionClient distributionClient,
        PermissionsCache cache,
        IOptions<CocoarAuthOptions> options,
        ILogger<CocoarAuthClaimsTransformation> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _distributionClient = distributionClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.AppSlug))
            throw new InvalidOperationException(
                "CocoarAuthOptions.AppSlug must be set. Configure it via AddCocoarAuthClient.");
        if (string.IsNullOrWhiteSpace(_options.IdpBaseUrl))
            throw new InvalidOperationException(
                "CocoarAuthOptions.IdpBaseUrl must be set. Configure it via AddCocoarAuthClient.");
        if (string.IsNullOrWhiteSpace(_options.ResourceServerId))
            throw new InvalidOperationException(
                "CocoarAuthOptions.ResourceServerId must be set. Configure it via AddCocoarAuthClient.");
        if (string.IsNullOrWhiteSpace(_options.ResourceServerSecret))
            throw new InvalidOperationException(
                "CocoarAuthOptions.ResourceServerSecret must be set. Configure it via AddCocoarAuthClient.");
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return principal;

        // Already transformed in this pipeline — skip the second call.
        if (identity.HasClaim(TransformedMarkerClaim, "1"))
            return principal;

        // sub + jti are mandatory for cache-key construction. If either is
        // missing the principal is shaped weirdly; bail out without
        // consulting the IdP rather than crashing.
        var sub = principal.FindFirst(Claims.Subject)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var jti = principal.FindFirst(Claims.JwtId)?.Value;
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(jti))
        {
            _logger.LogDebug("Cocoar.Auth: skipping distribution call — principal missing sub or jti.");
            return principal;
        }

        var bearerToken = ExtractBearerToken(_httpContextAccessor.HttpContext);
        if (string.IsNullOrEmpty(bearerToken))
        {
            _logger.LogDebug("Cocoar.Auth: skipping distribution call — no bearer token on the incoming request.");
            return principal;
        }

        try
        {
            var permissions = await _cache.GetOrFetchAsync(
                sub, jti, _options.AppSlug,
                ct => _distributionClient.GetMePermissionsAsync(bearerToken, ct),
                _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);

            ApplyToIdentity(identity, permissions);
            identity.AddClaim(new Claim(TransformedMarkerClaim, "1"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cocoar.Auth: distribution-API call failed for sub={Sub}. " +
                "Continuing without role/permission/group enrichment — " +
                "downstream gates will use whatever the bearer token carried.",
                sub);
        }

        return principal;
    }

    private static void ApplyToIdentity(ClaimsIdentity identity, MePermissionsResponse data)
    {
        var existingRoles = new HashSet<string>(
            identity.FindAll(ClaimTypes.Role).Select(c => c.Value),
            StringComparer.Ordinal);
        foreach (var role in data.Roles ?? [])
        {
            if (string.IsNullOrEmpty(role.Name) || !existingRoles.Add(role.Name)) continue;
            identity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
        }

        var existingPermissions = new HashSet<string>(
            identity.FindAll(PermissionClaimType).Select(c => c.Value),
            StringComparer.Ordinal);
        foreach (var permission in data.Permissions ?? [])
        {
            if (string.IsNullOrEmpty(permission) || !existingPermissions.Add(permission)) continue;
            identity.AddClaim(new Claim(PermissionClaimType, permission));
        }

        var existingGroups = new HashSet<string>(
            identity.FindAll(GroupClaimType).Select(c => c.Value),
            StringComparer.Ordinal);
        foreach (var group in data.Groups ?? [])
        {
            if (string.IsNullOrEmpty(group.Name) || !existingGroups.Add(group.Name)) continue;
            identity.AddClaim(new Claim(GroupClaimType, group.Name));
        }
    }

    private static string? ExtractBearerToken(HttpContext? httpContext)
    {
        var raw = httpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) return null;
        if (!AuthenticationHeaderValue.TryParse(raw, out var header)) return null;
        if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) return null;
        return header.Parameter;
    }
}
