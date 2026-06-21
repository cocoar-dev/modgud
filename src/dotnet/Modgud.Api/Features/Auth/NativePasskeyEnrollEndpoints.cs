using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// ADR-0009 — the cookieless, Bearer-authenticated native passkey ENROLLMENT
/// endpoint pair. The web register flow (<c>/api/account/passkey/register*</c>) is
/// cookie-authenticated and stashes the attestation options in the server-side
/// session — a native client (signed in via a <c>urn:cocoar:*</c> grant, holding an
/// access token, with no cookie/session) can use neither. These two endpoints mirror
/// the native LOGIN ceremony: authenticated by the OpenIddict validation (Bearer)
/// scheme, gated behind the per-realm <c>NativeGrants</c> flag, persisting a
/// single-use <see cref="PasskeyEnrollCeremony"/>.
///
/// <para>The enrolled credential is stored with the per-client RP ID
/// (<see cref="RpIdResolver"/> resolves the token's client), so it is byte-identical
/// to what the native login ceremony for the same client later demands — closing the
/// "authenticate once, then add a passkey for THIS app" bootstrap.</para>
/// </summary>
public static class NativePasskeyEnrollEndpoints
{
    private const string LoggerCategory = "Modgud.Api.Features.Auth.NativePasskeyEnrollEndpoints";

    public static WebApplication MapNativePasskeyEnrollEndpoints(this WebApplication application)
    {
        // POST /connect/passkey/enroll/begin — issue attestation options for the
        // authenticated user, under the requesting client's RP ID.
        application.MapPost("/connect/passkey/enroll/begin", async (
            HttpContext context,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            UserManager<ApplicationUser> userManager,
            RealmScopedFido2Factory fido2Factory,
            RpIdResolver rpIdResolver,
            CancellationToken ct) =>
        {
            if (await GateDisabledAsync(settingsResolver, context, ct) is { } gate) return gate;

            var (user, clientId, unauthorized) = await ResolvePrincipalAsync(context, userManager);
            if (unauthorized is not null) return unauthorized;

            var rpId = await rpIdResolver.ResolveAsync(session, clientId, ct);

            IFido2 fido2;
            try
            {
                fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: rpId);
            }
            catch (RelyingPartyUnavailableException ex)
            {
                return RpUnavailable(context, ex);
            }

            // Exclude the user's existing credentials FOR THIS RP ID so the same
            // authenticator does not enroll a duplicate (one passkey per app per user).
            var excludeCredentials = (await session.Query<StoredPasskeyCredential>()
                .Where(c => c.UserId == user!.Id)
                .ToListAsync(ct))
                .Where(c => string.Equals(c.RpId ?? rpId, rpId, StringComparison.OrdinalIgnoreCase))
                .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                .ToList();

            var fidoUser = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(user!.Id.ToString()),
                Name = user.UserName ?? user.Acronym ?? user.Id.ToString(),
                DisplayName = user.GetDisplayLabel(),
            };

            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = fidoUser,
                ExcludeCredentials = excludeCredentials,
                // Discoverable (resident) so the native usernameless login (empty
                // allowCredentials) can find it; Preferred — not Required — so an
                // authenticator without a resident slot still enrolls. Platform
                // authenticators (Face ID / Windows Hello / passkeys — the native
                // target) create discoverable credentials and perform UV under
                // Preferred, and the login ceremony enforces UV=Required at assertion.
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });

            // Opportunistic expiry sweep (same amortization as the login begin).
            session.DeleteWhere<PasskeyEnrollCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);

            var optionsJson = options.ToJson();
            var ceremony = new PasskeyEnrollCeremony
            {
                Id = Guid.NewGuid(),
                OptionsJson = optionsJson,
                UserId = user.Id,
                ClientId = clientId,
                RpId = rpId,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PasskeyEnrollCeremony.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(ceremony);
            await session.SaveChangesAsync(ct);

            // Verbatim FIDO2 JSON (Results.Content, not Json) so the WebAuthn
            // enum / base64url encodings survive.
            return Results.Content(
                $"{{\"ceremonyId\":\"{ceremony.Id}\",\"options\":{optionsJson}}}",
                "application/json");
        })
        .WithName("NativePasskey_EnrollBegin")
        .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        })
        .DisableAntiforgery()
        .WithTags("Native Auth")
        .RequireRateLimiting("passkey-begin");

        // POST /connect/passkey/enroll — verify attestation, store the credential
        // under the ceremony's pinned RP ID.
        application.MapPost("/connect/passkey/enroll", async (
            HttpContext context,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            UserManager<ApplicationUser> userManager,
            RealmScopedFido2Factory fido2Factory,
            JsonElement body,
            CancellationToken ct) =>
        {
            if (await GateDisabledAsync(settingsResolver, context, ct) is { } gate) return gate;

            var (user, clientId, unauthorized) = await ResolvePrincipalAsync(context, userManager);
            if (unauthorized is not null) return unauthorized;

            if (!body.TryGetProperty("ceremonyId", out var cidEl)
                || !Guid.TryParse(cidEl.GetString(), out var ceremonyId)
                || !body.TryGetProperty("attestation", out var attEl))
                return Results.BadRequest(new { Message = "Invalid enrollment request." });

            var ceremony = await session.LoadAsync<PasskeyEnrollCeremony>(ceremonyId, ct);
            // Bind the ceremony to the authenticated user and the requesting client.
            if (ceremony is null || ceremony.IsExpired
                || ceremony.UserId != user!.Id
                || (!string.IsNullOrEmpty(ceremony.ClientId)
                    && !string.Equals(ceremony.ClientId, clientId, StringComparison.Ordinal)))
            {
                if (ceremony is not null) { session.Delete(ceremony); await session.SaveChangesAsync(ct); }
                return Results.BadRequest(new { Message = "Enrollment session expired. Please try again." });
            }

            // Single-use: consume before verifying.
            session.Delete(ceremony);
            await session.SaveChangesAsync(ct);

            IFido2 fido2;
            try
            {
                // Pinned RP ID from begin — never re-resolved (admin edits mid-ceremony
                // cannot drift the attestation's RP ID).
                fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: ceremony.RpId);
            }
            catch (RelyingPartyUnavailableException ex)
            {
                return RpUnavailable(context, ex);
            }

            CredentialCreateOptions options;
            try
            {
                options = CredentialCreateOptions.FromJson(ceremony.OptionsJson);
            }
            catch
            {
                return Results.BadRequest(new { Message = "Enrollment session expired. Please try again." });
            }

            var attestation = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
                attEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (attestation is null)
                return Results.BadRequest(new { Message = "Invalid attestation response." });

            RegisteredPublicKeyCredential credential;
            try
            {
                credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = attestation,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (args, innerCt) =>
                    {
                        // CredentialId is a byte[]; Marten can't translate a byte[]
                        // equality into valid SQL (it casts the base64 to jsonb and
                        // fails) — load + compare in memory, as PasskeyAssertionVerifier does.
                        var all = await session.Query<StoredPasskeyCredential>().ToListAsync(innerCt);
                        return !all.Any(c => c.CredentialId.SequenceEqual(args.CredentialId));
                    },
                }, ct);
            }
            catch
            {
                // Fail closed on any attestation failure (bad/forged response,
                // duplicate credential) — never a 500.
                return Results.BadRequest(new { Message = "Passkey enrollment failed." });
            }

            var stored = new StoredPasskeyCredential
            {
                Id = Guid.CreateVersion7(),
                UserId = user!.Id,
                CredentialId = credential.Id,
                PublicKey = credential.PublicKey,
                UserHandle = credential.User.Id,
                SignatureCount = credential.SignCount,
                AttestationType = "none",
                AaGuid = credential.AaGuid,
                DisplayName = "Passkey",
                RpId = ceremony.RpId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(stored);
            await session.SaveChangesAsync(ct);

            return Results.Ok(new { Message = "Passkey enrolled successfully." });
        })
        .WithName("NativePasskey_Enroll")
        .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        })
        .DisableAntiforgery()
        .WithTags("Native Auth")
        .RequireRateLimiting("passkey-begin");

        return application;
    }

    /// <summary>Per-(App ⊕ realm) master gate (default OFF), ADR-0011. Bearer-
    /// authenticated, so the App is resolved client_id-time from the token's
    /// client (or the Host pin when on an Application subdomain).</summary>
    private static async Task<IResult?> GateDisabledAsync(
        IApplicationSettingsResolver settingsResolver, HttpContext context, CancellationToken ct)
    {
        var clientId = context.User.GetClaim(Claims.ClientId) ?? context.User.GetClaim(Claims.AuthorizedParty);
        var settings = await settingsResolver.ResolveForRequestAsync(context, clientId, ct);
        if (settings.NativeGrants is null || !settings.NativeGrants.Enabled)
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "NativeGrants.Disabled",
                detail: "Native passkey sign-in is not enabled for this realm.");
        return null;
    }

    /// <summary>
    /// Resolves the authenticated subject (store-backed so the SecurityStamp is
    /// authoritative, never trusting token claims as the user record) and the
    /// requesting client_id from the validated Bearer access token.
    /// </summary>
    private static async Task<(ApplicationUser? user, string? clientId, IResult? unauthorized)> ResolvePrincipalAsync(
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

    private static IResult RpUnavailable(HttpContext context, RelyingPartyUnavailableException ex)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(LoggerCategory)
            .LogError(ex, "Passkey enrollment unavailable for this realm: {Reason}", ex.Message);
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Passkey.Unavailable",
            detail: "Passkey enrollment is not available for this realm because its primary domain "
                  + "is not configured. Contact an administrator.");
    }
}
