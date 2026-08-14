using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using OpenIddict.Validation.AspNetCore;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// The cookieless, Bearer-authenticated native passkey MANAGEMENT pair — list and
/// delete the authenticated user's own passkeys. The cookie-based realm UI
/// (<c>GET/DELETE /api/account/passkey</c>) needs a Modgud session, which a native
/// client (or a brokering BFF holding only an access token) does not have; these two
/// mirror that surface for Bearer callers so a native/BFF profile can show "your
/// passkeys" and revoke a lost device — the missing half of the cookieless
/// enroll/login ceremony (ADR-0009 / ADR-0010).
///
/// <para>Authenticated by the OpenIddict validation (Bearer) scheme, gated behind the
/// per-realm <c>NativeGrants</c> flag, and strictly owner-scoped: a caller only ever
/// sees or deletes credentials owned by the token's subject. An unknown id or one
/// owned by another user is a <c>404</c> (never <c>403</c>) so the endpoint is not a
/// cross-user credential-existence oracle.</para>
/// </summary>
public static class NativePasskeyManagementEndpoints
{
    /// <summary>The owner-scoped projection returned by the list endpoint. <c>Id</c>
    /// is the <see cref="StoredPasskeyCredential.Id"/> (the addressable management id),
    /// never the raw WebAuthn CredentialId.</summary>
    public sealed record PasskeyListItem(string Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    public static WebApplication MapNativePasskeyManagementEndpoints(this WebApplication application)
    {
        // GET /connect/passkey — the token subject's own passkeys. User-scoped (every
        // credential the user holds, across RP IDs), mirroring the cookie-based
        // Passkey_List, so a "manage my passkeys / revoke a lost device" surface sees
        // exactly what the realm UI would.
        application.MapGet("/connect/passkey", async (
            HttpContext context,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            if (await NativeBearerEndpointSupport.GateDisabledAsync(settingsResolver, context, ct) is { } gate) return gate;

            var (user, _, unauthorized) = await NativeBearerEndpointSupport.ResolvePrincipalAsync(context, userManager);
            if (unauthorized is not null) return unauthorized;

            var credentials = await session.Query<StoredPasskeyCredential>()
                .Where(c => c.UserId == user!.Id)
                .ToListAsync(ct);

            return Results.Ok(credentials
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new PasskeyListItem(c.Id.ToString(), c.DisplayName, c.CreatedAt, c.LastUsedAt)));
        })
        .WithName("NativePasskey_List")
        .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        })
        .WithTags("Native Auth");

        // DELETE /connect/passkey/{id} — revoke ONE of the subject's own passkeys.
        // Owner-scoped: a credential owned by another user (or no such id) is a 404,
        // not a 403, so it cannot be used to probe another user's credentials. Once
        // deleted the credential can no longer satisfy a urn:cocoar:passkey assertion.
        application.MapDelete("/connect/passkey/{id:guid}", async (
            Guid id,
            HttpContext context,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            UserManager<ApplicationUser> userManager,
            Modgud.Infrastructure.PositionTerminals.IStaffingRevoker staffingRevoker,
            CancellationToken ct) =>
        {
            if (await NativeBearerEndpointSupport.GateDisabledAsync(settingsResolver, context, ct) is { } gate) return gate;

            var (user, _, unauthorized) = await NativeBearerEndpointSupport.ResolvePrincipalAsync(context, userManager);
            if (unauthorized is not null) return unauthorized;

            var credential = await session.LoadAsync<StoredPasskeyCredential>(id, ct);
            if (credential is null || credential.UserId != user!.Id)
                return Results.NotFound();

            session.Delete(credential);
            await session.SaveChangesAsync(ct);

            // MG-FT-07 §15.4 — staffing sessions opened with THIS credential
            // end with it: the shift's trust anchor (the tap) is gone.
            await staffingRevoker.EndAllForPasskeyAsync(
                credential.Id, Modgud.Domain.PositionTerminals.StaffingSessionEndReason.PasskeyDeleted, ct);

            return Results.NoContent();
        })
        .WithName("NativePasskey_Delete")
        .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        })
        .DisableAntiforgery()
        .WithTags("Native Auth");

        return application;
    }
}
