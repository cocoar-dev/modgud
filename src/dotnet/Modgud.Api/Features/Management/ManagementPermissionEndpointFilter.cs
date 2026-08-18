using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Management;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Management;

/// <summary>
/// Authorizes one deliberately exposed Modgud management operation for either
/// the first-party admin cookie or an OAuth access token. OAuth scopes select
/// the management API; the system-App permission remains the single source of
/// fine-grained authorization for both Persons and ServiceAccounts.
/// </summary>
public sealed class ManagementPermissionEndpointFilter(
    string permission,
    string? clientAppRouteParameter = null) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var bearerRequest = IsBearerRequest(http.Request);
        var scheme = bearerRequest
            ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme;

        var authentication = await http.AuthenticateAsync(scheme);
        if (!authentication.Succeeded || authentication.Principal is null)
            return Results.Unauthorized();

        var caller = authentication.Principal;
        if (bearerRequest && await ValidateBearerCallerAsync(http, caller) is { } bearerError)
            return bearerError;

        var subject = caller.GetClaim(Claims.Subject)
                      ?? caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var principalId))
            return Results.Unauthorized();

        var principalLookup = http.RequestServices.GetRequiredService<IPrincipalLookupService>();
        var principal = await principalLookup.GetByIdAsync(principalId, http.RequestAborted);
        if (principal is null || !principal.IsActive)
            return Results.Unauthorized();

        if (bearerRequest && principal is not Person and not ServiceAccount)
            return Forbidden("Management.UnsupportedPrincipal",
                "Management access tokens must represent a Person or Service Account.");

        if (bearerRequest && principal is ServiceAccount serviceAccount &&
            await ValidateServiceAccountClientAsync(http, caller, serviceAccount) is { } clientError)
            return clientError;

        if (bearerRequest && principal is Person &&
            await ValidateDelegatedClientAsync(http, caller) is { } delegatedClientError)
            return delegatedClientError;

        if (bearerRequest && clientAppRouteParameter is not null &&
            await ValidateClientAppBoundaryAsync(http, caller, clientAppRouteParameter)
                is { } appBoundaryError)
        {
            return appBoundaryError;
        }

        var permissionService = http.RequestServices.GetRequiredService<IPermissionService>();
        if (!await permissionService.HasPermissionAsync(
                principalId, AppSlugs.Modgud, permission, http.RequestAborted))
        {
            return Forbidden("Management.PermissionDenied",
                $"Missing the required permission '{permission}' (app '{AppSlugs.Modgud}').");
        }

        // Downstream handlers and audit helpers must see the explicitly selected
        // identity, never an accidental merge of cookie + bearer identities.
        http.User = caller;
        return await next(context);
    }

    private static async Task<IResult?> ValidateBearerCallerAsync(
        HttpContext http,
        ClaimsPrincipal caller)
    {
        if (!caller.GetAudiences().Contains(ModgudManagementApi.Audience, StringComparer.Ordinal))
        {
            return Forbidden("Management.InvalidAudience",
                $"The access token is not intended for '{ModgudManagementApi.Audience}'.");
        }

        if (!caller.HasScope(ModgudManagementApi.Scope))
        {
            return Forbidden("Management.MissingScope",
                $"The access token is missing the '{ModgudManagementApi.Scope}' scope.");
        }

        return string.IsNullOrWhiteSpace(ResolveClientId(caller))
            ? Results.Unauthorized()
            : null;
    }

    private static async Task<IResult?> ValidateServiceAccountClientAsync(
        HttpContext http,
        ClaimsPrincipal caller,
        ServiceAccount serviceAccount)
    {
        var client = await LoadClientAsync(http, caller);
        if (ValidateRegisteredClient(client) is { } registrationError)
            return registrationError;

        if (client!.LinkedServiceAccountId != serviceAccount.Id)
        {
            return Forbidden("Management.ServiceAccountClientMismatch",
                "The OAuth client is not linked to the Service Account represented by the token.");
        }

        return null;
    }

    private static async Task<IResult?> ValidateDelegatedClientAsync(
        HttpContext http,
        ClaimsPrincipal caller)
    {
        var client = await LoadClientAsync(http, caller);
        if (ValidateRegisteredClient(client) is { } registrationError)
            return registrationError;

        // Grant separation is an issuance invariant, and is repeated here so a
        // stale or malformed token can never turn an M2M credential into a
        // delegated-user management client.
        if (client!.LinkedServiceAccountId.HasValue)
        {
            return Forbidden("Management.DelegatedClientRequired",
                "A delegated user token must be issued to a user-flow OAuth client.");
        }

        return null;
    }

    private static IResult? ValidateRegisteredClient(OAuthApplicationState? client)
    {
        if (client is null ||
            !GetBooleanProperty(client, OAuthApplicationPropertyKeys.Enabled, defaultValue: true))
        {
            return Results.Unauthorized();
        }

        // Management clients are an administrator-issued trust decision. DCR
        // already prevents opting into the protected scope; repeat the invariant
        // at the resource boundary so malformed legacy events cannot bypass it.
        if (GetBooleanProperty(
                client,
                OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered,
                defaultValue: false))
        {
            return Forbidden("Management.AdminRegisteredClientRequired",
                "Dynamically registered clients cannot call the Modgud management API.");
        }

        var managementScopePermission =
            OpenIddictConstants.Permissions.Prefixes.Scope + ModgudManagementApi.Scope;
        if (!client.Permissions.Contains(managementScopePermission, StringComparer.Ordinal))
        {
            return Forbidden("Management.ClientScopeRevoked",
                $"The OAuth client is no longer allowed to request '{ModgudManagementApi.Scope}'.");
        }

        return null;
    }

    private static async Task<IResult?> ValidateClientAppBoundaryAsync(
        HttpContext http,
        ClaimsPrincipal caller,
        string routeParameter)
    {
        var raw = http.Request.RouteValues.TryGetValue(routeParameter, out var value)
            ? value?.ToString()
            : null;
        if (raw is null || !ShortGuid.TryParse(raw, out Guid targetAppId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Application id",
                detail: $"The route value '{{{routeParameter}}}' is not a valid Application id.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "Management.InvalidTargetApp",
                });
        }

        var client = await LoadClientAsync(http, caller);
        if (client is null || !client.AppIds.Contains(targetAppId))
        {
            return Forbidden(
                "Management.ClientAppMismatch",
                $"The OAuth client is not assigned to Application '{new ShortGuid(targetAppId)}'.");
        }

        return null;
    }

    private static async Task<OAuthApplicationState?> LoadClientAsync(
        HttpContext http,
        ClaimsPrincipal caller)
    {
        var clientId = ResolveClientId(caller);
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var session = http.RequestServices.GetRequiredService<IQuerySession>();
        return await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(
                candidate => candidate.ClientId == clientId && !candidate.IsDeleted,
                http.RequestAborted);
    }

    private static string? ResolveClientId(ClaimsPrincipal caller) =>
        caller.GetClaim(Claims.ClientId) ?? caller.GetClaim(Claims.AuthorizedParty);

    private static bool GetBooleanProperty(
        OAuthApplicationState client,
        string key,
        bool defaultValue)
    {
        if (!client.Properties.TryGetValue(key, out var raw) || raw is null)
            return defaultValue;

        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => defaultValue,
        };
    }

    private static bool IsBearerRequest(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var value))
            return false;
        return string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Forbidden(string code, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public static class ManagementPermissionEndpointExtensions
{
    private static readonly string AuthenticationSchemes =
        $"{IdentityConstants.ApplicationScheme}," +
        OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    /// <summary>
    /// Opts an endpoint into the Modgud management API. This is intentionally
    /// explicit: ordinary cookie-only admin endpoints do not become remotely
    /// callable merely because the management authentication scheme exists.
    /// </summary>
    public static RouteHandlerBuilder RequiresManagementPermission(
        this RouteHandlerBuilder builder,
        string permission,
        string? clientAppRouteParameter = null)
    {
        builder.RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = AuthenticationSchemes,
        });
        builder.AddEndpointFilter(new ManagementPermissionEndpointFilter(
            permission,
            clientAppRouteParameter));
        return builder;
    }
}
