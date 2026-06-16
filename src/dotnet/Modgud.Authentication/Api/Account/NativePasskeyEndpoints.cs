using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// ADR-0010 Phase 2 — the dedicated cookieless WebAuthn "begin" endpoint for the
/// native <c>urn:cocoar:passkey</c> grant. Anonymous + rate-limited; gated behind
/// the per-realm <c>NativeGrants</c> flag (default OFF), the same master gate the
/// <c>urn:cocoar:*</c> token grants check. Issues a discoverable/usernameless
/// <c>AssertionOptions</c> (empty allowCredentials, UserVerification=Required),
/// persists it as a single-use <see cref="PasskeyCeremony"/> doc, and returns
/// <c>{ceremonyId, options}</c> as verbatim FIDO2 JSON. The client signs the
/// challenge on-device and redeems <c>{ceremony_id, assertion}</c> at
/// <c>/connect/token</c> (<c>grant_type=urn:cocoar:passkey</c>).
/// </summary>
public static class NativePasskeyEndpoints
{
    public static WebApplication MapNativePasskeyEndpoints(this WebApplication application)
    {
        application.MapPost("/connect/passkey/begin", async (
            HttpContext context,
            IDocumentSession session,
            RealmScopedFido2Factory fido2Factory,
            CancellationToken ct) =>
        {
            // Per-realm master gate (default OFF). The injected session is
            // tenant-scoped (RealmMiddleware); a null section reads as disabled.
            // Gating here (not just at the token grant) avoids issuing useless
            // ceremony docs when the realm hasn't opted into native passwordless.
            var settings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
            if (settings?.NativeGrants is null || !settings.NativeGrants.Enabled)
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "NativeGrants.Disabled",
                    detail: "Native passkey sign-in is not enabled for this realm.");

            IFido2 fido2;
            try
            {
                fido2 = await fido2Factory.CreateAsync(ct);
            }
            catch (RelyingPartyUnavailableException ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Modgud.Authentication.Api.Account.NativePasskeyEndpoints")
                    .LogError(ex, "Passkey ceremony unavailable for this realm: {Reason}", ex.Message);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Passkey.Unavailable",
                    detail: "Passkey sign-in is not available for this realm because its primary domain "
                          + "is not configured. Contact an administrator.");
            }

            // Discoverable / usernameless: empty allowCredentials (a per-account
            // allow-list on an anonymous endpoint would be a credential-existence
            // oracle) + UserVerification=Required so a verified assertion is
            // genuinely multi-factor (device possession + biometric/PIN) — which is
            // why the urn:cocoar:passkey grant does not also demand a TOTP code.
            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = [],
                UserVerification = UserVerificationRequirement.Required,
            });

            // Opportunistic cleanup: this anonymous endpoint creates ceremony docs
            // that are usually never redeemed, so amortize expiry cleanup onto the
            // same traffic that creates them — bounds orphaned-ceremony growth in
            // the per-realm DB without a scheduled job. Backed by the ExpiresAt
            // index; committed in the single SaveChanges below alongside the new doc.
            session.DeleteWhere<PasskeyCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);

            var optionsJson = options.ToJson();
            var ceremony = new PasskeyCeremony
            {
                Id = Guid.NewGuid(),
                OptionsJson = optionsJson,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PasskeyCeremony.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(ceremony);
            await session.SaveChangesAsync(ct);

            // Return the FIDO2 options JSON VERBATIM (Results.Content, NOT Json) so
            // the WebAuthn enum / base64url encodings survive — routing it through
            // ASP.NET's serializer would corrupt them.
            return Results.Content(
                $"{{\"ceremonyId\":\"{ceremony.Id}\",\"options\":{optionsJson}}}",
                "application/json");
        })
        .WithName("NativePasskey_Begin")
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("Native Auth")
        .RequireRateLimiting("passkey-begin");

        return application;
    }
}
