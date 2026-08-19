using System.Net.Http.Headers;
using System.Security.Claims;
using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
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
        if (bearerRequest)
        {
            Guid? targetAppId = null;
            if (clientAppRouteParameter is not null)
            {
                var raw = http.Request.RouteValues.TryGetValue(
                    clientAppRouteParameter, out var value)
                    ? value?.ToString()
                    : null;
                if (raw is null || !ShortGuid.TryParse(raw, out Guid parsedAppId))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid Application id",
                        detail: $"The route value '{{{clientAppRouteParameter}}}' is not a valid Application id.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "Management.InvalidTargetApp",
                        });
                }
                targetAppId = parsedAppId;
            }

            var authorization = http.RequestServices
                .GetRequiredService<ManagementBearerAuthorizationService>();
            var denied = await authorization.AuthorizeAsync(
                caller, targetAppId, permission, http.RequestAborted);
            if (denied is not null) return BearerDenied(denied);

            // Downstream handlers and audit helpers must see the explicitly
            // selected bearer identity, never an accidental cookie merge.
            http.User = caller;
            return await next(context);
        }

        var subject = caller.GetClaim(Claims.Subject)
                      ?? caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var principalId))
            return Results.Unauthorized();

        var principalLookup = http.RequestServices.GetRequiredService<IPrincipalLookupService>();
        var principal = await principalLookup.GetByIdAsync(principalId, http.RequestAborted);
        if (principal is null || !principal.IsActive)
            return Results.Unauthorized();

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

    private static IResult BearerDenied(ManagementBearerAuthorizationError error)
    {
        if (error.Code is "invalid_client" or "invalid_subject" or "inactive_subject"
            or "token_expired")
            return Results.Unauthorized();

        var publicCode = error.Code switch
        {
            "invalid_audience" => "Management.InvalidAudience",
            "missing_scope" => "Management.MissingScope",
            "unsupported_subject" => "Management.UnsupportedPrincipal",
            "admin_registered_client_required" => "Management.AdminRegisteredClientRequired",
            "client_scope_revoked" => "Management.ClientScopeRevoked",
            "service_account_client_mismatch" => "Management.ServiceAccountClientMismatch",
            "delegated_client_required" => "Management.DelegatedClientRequired",
            "client_app_mismatch" => "Management.ClientAppMismatch",
            "permission_denied" => "Management.PermissionDenied",
            _ => "Management.Forbidden",
        };

        return Forbidden(publicCode, error.Detail);
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
        string? clientAppRouteParameter = null,
        bool bearerOnly = false)
    {
        builder.RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = bearerOnly
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                : AuthenticationSchemes,
        });
        builder.AddEndpointFilter(new ManagementPermissionEndpointFilter(
            permission,
            clientAppRouteParameter));
        return builder;
    }
}
