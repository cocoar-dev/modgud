using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// Shared building blocks for the cookieless, Bearer-authenticated native passkey
/// endpoints (enroll / list / delete). The per-realm <c>NativeGrants</c> master gate
/// and the store-backed principal resolution are identical across them, so they live
/// here once — the security contract (which user a token speaks for, whether the
/// feature is enabled) must never drift between the management and enrollment paths.
/// </summary>
internal static class NativeBearerEndpointSupport
{
    /// <summary>
    /// Per-(App ⊕ realm) <c>NativeGrants</c> master gate (default OFF), ADR-0011.
    /// Bearer-authenticated, so the App is resolved client_id-time from the token's
    /// client (or the Host pin when on an Application subdomain). Returns a 400
    /// problem result when disabled, <c>null</c> when the caller may proceed.
    /// </summary>
    public static async Task<IResult?> GateDisabledAsync(
        IApplicationSettingsResolver settingsResolver, HttpContext context, CancellationToken ct)
    {
        var clientId = context.User.GetClaim(Claims.ClientId) ?? context.User.GetClaim(Claims.AuthorizedParty);
        var settings = await settingsResolver.ResolveForRequestAsync(context, clientId, ct);
        if (settings.NativeGrants is null || !settings.NativeGrants.Enabled)
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "NativeGrants.Disabled",
                detail: "Native passwordless features are not enabled for this realm.");
        return null;
    }

    /// <summary>
    /// Resolves the authenticated subject (store-backed so the SecurityStamp / active
    /// / deleted state is authoritative, never trusting token claims as the user
    /// record) and the requesting client_id from the validated Bearer access token.
    /// Returns a 401 result when there is no usable subject.
    /// </summary>
    public static async Task<(ApplicationUser? user, string? clientId, IResult? unauthorized)> ResolvePrincipalAsync(
        HttpContext context, UserManager<ApplicationUser> userManager)
    {
        var sub = context.User.GetClaim(Claims.Subject);
        var clientId = context.User.GetClaim(Claims.ClientId) ?? context.User.GetClaim(Claims.AuthorizedParty);
        if (string.IsNullOrEmpty(sub))
            return (null, null, Results.Unauthorized());

        var user = await userManager.FindByIdAsync(sub);
        if (user is null || !user.IsActive || user.IsDeleted)
            return (null, null, Results.Unauthorized());

        return (user, clientId, null);
    }
}
