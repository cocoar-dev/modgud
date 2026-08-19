using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Marten;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Management;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Management;

/// <summary>
/// Reusable bearer half of the Management API boundary. Long-lived transports
/// must repeat these checks after the minimal-API endpoint filter has returned,
/// so the client, subject, AppIds, and permission invariants live in one guard.
/// </summary>
public sealed class ManagementBearerAuthorizationService(
    IQuerySession session,
    IPrincipalLookupService principalLookup,
    IPermissionService permissionService)
{
    public async Task<ManagementBearerAuthorizationError?> AuthorizeAsync(
        ClaimsPrincipal caller,
        Guid? targetAppId,
        string permission,
        CancellationToken cancellationToken)
    {
        var expiration = caller.FindFirstValue("exp");
        if (long.TryParse(expiration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
            && DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow)
        {
            return new("token_expired", "The access token has expired.");
        }

        if (!caller.GetAudiences().Contains(ModgudManagementApi.Audience, StringComparer.Ordinal))
            return new("invalid_audience", $"The token is not intended for '{ModgudManagementApi.Audience}'.");
        if (!caller.HasScope(ModgudManagementApi.Scope))
            return new("missing_scope", $"The token is missing '{ModgudManagementApi.Scope}'.");

        var clientId = caller.GetClaim(Claims.ClientId) ?? caller.GetClaim(Claims.AuthorizedParty);
        if (string.IsNullOrWhiteSpace(clientId))
            return new("invalid_client", "The token does not identify its OAuth client.");

        var subject = caller.GetClaim(Claims.Subject)
                      ?? caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var principalId))
            return new("invalid_subject", "The token subject is not a Modgud principal.");

        var principal = await principalLookup.GetByIdAsync(principalId, cancellationToken);
        if (principal is null || !principal.IsActive || principal.IsDeleted)
            return new("inactive_subject", "The token subject is not an active principal.");
        if (principal is not Person and not ServiceAccount)
            return new("unsupported_subject", "Management tokens must represent a Person or Service Account.");

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId && !x.IsDeleted, cancellationToken);
        if (client is null || !BooleanProperty(client, OAuthApplicationPropertyKeys.Enabled, true))
            return new("invalid_client", "The OAuth client is missing or disabled.");
        if (BooleanProperty(client, OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered, false))
            return new("admin_registered_client_required", "Dynamically registered clients cannot use the Management API.");

        var managementScopePermission =
            OpenIddictConstants.Permissions.Prefixes.Scope + ModgudManagementApi.Scope;
        if (!client.Permissions.Contains(managementScopePermission, StringComparer.Ordinal))
            return new("client_scope_revoked", $"The client is no longer allowed to request '{ModgudManagementApi.Scope}'.");

        if (principal is ServiceAccount account && client.LinkedServiceAccountId != account.Id)
            return new("service_account_client_mismatch", "The client is not linked to the Service Account in the token.");
        if (principal is Person && client.LinkedServiceAccountId.HasValue)
            return new("delegated_client_required", "A delegated Person token must use a user-flow OAuth client.");
        if (targetAppId is { } appId && !client.AppIds.Contains(appId))
            return new("client_app_mismatch", "The client is not assigned to the requested Application.");

        if (!await permissionService.HasPermissionAsync(
                principalId, AppSlugs.Modgud, permission, cancellationToken))
        {
            return new("permission_denied", $"Missing '{permission}' in the Modgud Application.");
        }

        return null;
    }

    private static bool BooleanProperty(
        OAuthApplicationState client,
        string key,
        bool defaultValue)
    {
        if (!client.Properties.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => defaultValue,
        };
    }
}

public sealed record ManagementBearerAuthorizationError(string Code, string Detail);
