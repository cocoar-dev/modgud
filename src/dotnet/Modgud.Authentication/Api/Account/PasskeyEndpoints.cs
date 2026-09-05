using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;

namespace Modgud.Authentication.Api.Account;

public static class PasskeyEndpoints
{
    public record PasskeyLoginOptionsRequest(string? UserName = null, string? ReturnUrl = null);
    public record PasskeyDisplayDto(string Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    /// <summary>Carries only the server-side ceremony id — never the ceremony
    /// state itself.</summary>
    private const string PasskeyChallengeCookie = "Modgud.Passkey.Challenge";

    /// <summary>Registration counterpart of <see cref="PasskeyChallengeCookie"/>:
    /// the id of the server-side <see cref="PasskeyEnrollCeremony"/> issued by
    /// register-options. The attestation options used to live in the ASP.NET
    /// session (in-memory, per node); with two instances the register call could
    /// land on a node that never saw them (ADR 0010, D6).</summary>
    private const string PasskeyEnrollCookie = "Modgud.Passkey.Enroll";

    /// <summary>The cookie is scoped to the passkey endpoints; every Append AND
    /// Delete must use this exact path or the delete silently does nothing.</summary>
    private const string PasskeyCookiePath = "/api/account/passkey";

    public static WebApplication MapPasskeyEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/passkey")
            .WithTags("Passkey / WebAuthn");

        // A WebAuthn ceremony needs the realm's PrimaryDomain as the relying-party
        // ID. If the realm has none (a deployment misconfiguration / migration gap
        // the create/update/boot invariants normally prevent), building the RP
        // throws — without this filter every passkey endpoint would surface that
        // as an opaque 500. Map it to a clear, actionable response and log it so
        // it shows up in the per-realm error feed.
        group.AddEndpointFilter(async (invocationContext, next) =>
        {
            try
            {
                return await next(invocationContext);
            }
            catch (RelyingPartyUnavailableException ex)
            {
                var http = invocationContext.HttpContext;
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Modgud.Authentication.Api.Account.PasskeyEndpoints")
                    .LogError(ex, "Passkey ceremony unavailable for this realm: {Reason}", ex.Message);

                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Passkey.Unavailable",
                    detail: "Passkey sign-in is not available for this realm because its primary domain "
                          + "is not configured. Contact an administrator.");
            }
        });

        // ═══ Registration (requires authentication) ═══

        // GET /api/account/passkey — List registered passkeys for current user
        group.MapGet("", [Authorize] async (
            HttpContext context,
            IDocumentSession session) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var credentials = await session.Query<StoredPasskeyCredential>()
                .Where(c => c.UserId == userId.Value)
                .ToListAsync();

            return Results.Ok(credentials.Select(c => new PasskeyDisplayDto(
                c.Id.ToString(), c.DisplayName, c.CreatedAt, c.LastUsedAt)));
        })
        .WithName("Passkey_List");

        // POST /api/account/passkey/register-options — Generate attestation options
        group.MapPost("register-options", [Authorize] async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            RpIdResolver rpIdResolver,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            // RP = current realm's primary domain. Built per request so the
            // same RP is used to create and (later) verify the credential.
            var fido2 = await fido2Factory.CreateAsync(ct);

            // ADR-0009: the web register flow is realm-scoped (RP = PrimaryDomain).
            // Exclude only the user's realm-RP credentials so a native per-client
            // credential (a different RP) is not referenced in this ceremony.
            var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
            var existingCredentials = (await session.Query<StoredPasskeyCredential>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync())
                .Where(c => string.Equals(c.RpId ?? primaryDomain, primaryDomain, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var excludeCredentials = existingCredentials
                .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                .ToList();

            var fidoUser = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(user.Id.ToString()),
                Name = user.UserName ?? user.Acronym ?? user.Id.ToString(),
                DisplayName = user.GetDisplayLabel(),
            };

            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = fidoUser,
                ExcludeCredentials = excludeCredentials,
                // ADR-0010: enroll a DISCOVERABLE (resident-key) credential so the
                // native usernameless begin (empty allowCredentials) can find it on
                // the device. Preferred — not Required — so authenticators without a
                // resident-key slot still enroll; platform authenticators (Face ID /
                // Windows Hello / iOS/Android passkeys, the native target) create
                // discoverable credentials under Preferred. UserVerification stays
                // Preferred here; the native login ceremony enforces UV=Required,
                // and platform authenticators perform UV at assertion time.
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });

            var json = options.ToJson();

            // Same server-side ceremony document the native enrollment uses; the
            // cookie carries only its id. Opportunistic cleanup of expired rows on
            // the same traffic that creates them (backed by the ExpiresAt index).
            session.DeleteWhere<PasskeyEnrollCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);
            var ceremony = new PasskeyEnrollCeremony
            {
                Id = Guid.NewGuid(),
                OptionsJson = json,
                UserId = user.Id,
                ClientId = null,
                RpId = primaryDomain,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PasskeyEnrollCeremony.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(ceremony);
            await session.SaveChangesAsync(ct);

            context.Response.Cookies.Append(PasskeyEnrollCookie,
                ceremony.Id.ToString(),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromMinutes(PasskeyEnrollCeremony.ExpirationMinutes),
                    Path = PasskeyCookiePath,
                });

            // Return Fido2's own JSON serialization — ASP.NET's JsonStringEnumConverter
            // would break WebAuthn enum values (e.g. "PublicKey" instead of "public-key")
            return Results.Content(json, "application/json");
        })
        .WithName("Passkey_RegisterOptions");

        // POST /api/account/passkey/register — Verify attestation and store credential
        group.MapPost("register", [Authorize] async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            IDocumentSession session,
            JsonElement body,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            // The cookie carries only the ceremony id; the authoritative options
            // come from the server-side record. Delete MUST repeat the Path the
            // cookie was set with or the stale cookie survives in the browser.
            var enrollCookie = context.Request.Cookies[PasskeyEnrollCookie];
            context.Response.Cookies.Delete(PasskeyEnrollCookie,
                new CookieOptions { Path = PasskeyCookiePath });

            if (!Guid.TryParse(enrollCookie, out var ceremonyId))
                return Results.BadRequest(new { Message = "Registration session expired. Please try again." });

            var ceremony = await session.LoadAsync<PasskeyEnrollCeremony>(ceremonyId, ct);
            if (ceremony is null || ceremony.IsExpired || ceremony.UserId != userId.Value)
            {
                if (ceremony is not null) { session.Delete(ceremony); await session.SaveChangesAsync(ct); }
                return Results.BadRequest(new { Message = "Registration session expired. Please try again." });
            }

            // Single-use: consume before verifying.
            session.Delete(ceremony);
            await session.SaveChangesAsync(ct);

            // Pinned at register-options so an admin editing PrimaryDomain
            // mid-ceremony cannot cause a begin/finish RP-ID drift.
            var fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: ceremony.RpId);

            var options = CredentialCreateOptions.FromJson(ceremony.OptionsJson);

            var fido2JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(body.GetRawText(), fido2JsonOptions);
            if (attestationResponse is null)
                return Results.BadRequest(new { Message = "Invalid attestation response." });

            var credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                {
                    // Audit #33 — compare CredentialId (byte[]) IN MEMORY. A Marten
                    // LINQ `c.CredentialId == args.CredentialId` is untranslatable
                    // (Postgres 22P02), so the uniqueness guard would throw on enroll
                    // (web register 500) instead of detecting a duplicate. Mirror the
                    // login path: materialize, then SequenceEqual.
                    var all = await session.Query<StoredPasskeyCredential>().ToListAsync(ct);
                    return !all.Any(c => c.CredentialId.SequenceEqual(args.CredentialId));
                },
            });

            var stored = new StoredPasskeyCredential
            {
                Id = Guid.CreateVersion7(),
                UserId = userId.Value,
                CredentialId = credential.Id,
                PublicKey = credential.PublicKey,
                UserHandle = credential.User.Id,
                SignatureCount = credential.SignCount,
                AttestationType = "none",
                AaGuid = credential.AaGuid,
                DisplayName = "Passkey",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(stored);
            await session.SaveChangesAsync();

            return Results.Ok(new { Message = "Passkey registered successfully." });
        })
        .WithName("Passkey_Register");

        // DELETE /api/account/passkey/{id} — Remove a passkey
        group.MapDelete("{id}", [Authorize] async (
            Guid id,
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            IAuthSettings appSettings,
            IDocumentSession session,
            Modgud.Infrastructure.PositionTerminals.IStaffingRevoker staffingRevoker) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var credential = await session.LoadAsync<StoredPasskeyCredential>(id);
            if (credential is null || credential.UserId != userId.Value)
                return Results.NotFound();

            // If this was the last 2FA method (last passkey + no other types) and
            // enforcement is active, expire the grace immediately so the user lands on
            // the blocking setup modal next login. The user is warned in the UI before
            // they get here.
            var secureSetupRequired = false;
            if (appSettings.AuthenticationMinimumLevel >= 1)
            {
                var user = await userManager.GetUserAsync(context.User);
                if (user is not null)
                {
                    var methods = await TwoFactorHelper.GetMethodsAsync(user, session);
                    var passkeyCount = await session.Query<StoredPasskeyCredential>()
                        .Where(c => c.UserId == userId.Value)
                        .CountAsync();
                    var nonPasskey = methods.Count(m => m != "passkey");
                    var willHaveZeroMethods = nonPasskey == 0 && passkeyCount <= 1;
                    if (willHaveZeroMethods)
                        secureSetupRequired = await TwoFactorHelper.ExpireSetupGraceAsync(user.Id, session);
                }
            }

            session.Delete(credential);
            await session.SaveChangesAsync();

            // MG-FT-07 §15.4 — staffing sessions opened with THIS credential
            // end with it: the shift's trust anchor (the tap) is gone.
            await staffingRevoker.EndAllForPasskeyAsync(
                credential.Id, Modgud.Domain.PositionTerminals.StaffingSessionEndReason.PasskeyDeleted);

            return Results.Ok(new { SecureSetupRequired = secureSetupRequired });
        })
        .WithName("Passkey_Delete");

        // ═══ Login (anonymous) ═══

        // POST /api/account/passkey/login-options — Generate assertion options
        group.MapPost("login-options", async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            RpIdResolver rpIdResolver,
            IApplicationSettingsResolver settingsResolver,
            IDocumentSession session,
            PasskeyLoginOptionsRequest? request,
            CancellationToken ct) =>
        {
            var clientId = Api.ExternalAuth.ExternalAuthEndpoints.ExtractAuthorizeClientId(request?.ReturnUrl);
            if ((await settingsResolver.ResolveForRequestAsync(context, clientId, ct))
                .LoginExperience?.InternalLoginEnabled == false)
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "LoginExperience.InternalDisabled",
                    detail: "Internal login is disabled for this application.");

            var fido2 = await fido2Factory.CreateAsync(ct);

            List<PublicKeyCredentialDescriptor>? allowedCredentials = null;

            if (!string.IsNullOrEmpty(request?.UserName))
            {
                var user = await session.Query<ApplicationUser>()
                    .FirstOrDefaultAsync(u => u.NormalizedUserName == request.UserName.ToUpperInvariant());

                if (user is not null)
                {
                    // ADR-0009: realm-scoped web login only surfaces the user's
                    // realm-RP credentials (a native per-client credential is bound
                    // to a different RP and is unusable here).
                    var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
                    var credentials = (await session.Query<StoredPasskeyCredential>()
                        .Where(c => c.UserId == user.Id)
                        .ToListAsync())
                        .Where(c => string.Equals(c.RpId ?? primaryDomain, primaryDomain, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    allowedCredentials = credentials
                        .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                        .ToList();
                }
            }

            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = allowedCredentials ?? [],
                // Audit #16 — Required, not Preferred. A passkey is treated as a full
                // (MFA-grade) credential here, so the assertion MUST prove user
                // verification (biometric/PIN). With Preferred the authenticator may
                // skip UV and MakeAssertionAsync would still accept it, downgrading a
                // passkey login to single-factor "possession only". Required makes the
                // library reject a non-UV assertion.
                UserVerification = UserVerificationRequirement.Required,
            });

            // The ceremony state lives SERVER-SIDE, exactly like the native
            // /connect/passkey/begin flow — the cookie only carries an opaque
            // ceremony id. It used to carry the whole AssertionOptions as plain
            // Base64 (no signature, no encryption) and the login endpoint parsed it
            // back as trusted input, so a client could rewrite the ceremony options
            // and replay an old challenge. Anonymous users have no session, but they
            // don't need one: the id is a lookup key, and tampering with it merely
            // fails to resolve.
            //
            // Opportunistic cleanup on the same traffic that creates the docs, as
            // the native begin endpoint does — bounds orphaned-ceremony growth
            // without a scheduled job (backed by the ExpiresAt index).
            session.DeleteWhere<PasskeyCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);

            var optionsJson = options.ToJson();
            var ceremony = new PasskeyCeremony
            {
                Id = Guid.NewGuid(),
                OptionsJson = optionsJson,
                // Realm-scoped web login: no per-client binding, and the RP ID is
                // pinned so an admin editing the setting mid-ceremony can't cause a
                // begin/redeem drift (same rationale as the native flow).
                ClientId = clientId,
                RpId = await rpIdResolver.GetPrimaryDomainAsync(ct),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PasskeyCeremony.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(ceremony);
            await session.SaveChangesAsync(ct);

            // Secure=Request.IsHttps mirrors CookieSecurePolicy.SameAsRequest:
            // the cookie carries Secure on HTTPS requests and not on plain HTTP
            // (dev only). With ForwardedHeaders middleware behind the reverse
            // proxy, IsHttps reflects the public scheme, so production deploys
            // always get Secure even when Kestrel itself listens on HTTP behind
            // the proxy.
            context.Response.Cookies.Append(PasskeyChallengeCookie,
                ceremony.Id.ToString(),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromMinutes(PasskeyCeremony.ExpirationMinutes),
                    Path = PasskeyCookiePath,
                });

            return Results.Content(optionsJson, "application/json");
        })
        .WithName("Passkey_LoginOptions")
        .AllowAnonymous();

        // POST /api/account/passkey/login — Verify assertion and sign in
        group.MapPost("login", async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            RpIdResolver rpIdResolver,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IApplicationSettingsResolver settingsResolver,
            JsonElement body,
            CancellationToken ct) =>
        {
            // The cookie carries only the ceremony id; the authoritative options
            // come from the server-side record. Delete MUST repeat the Path the
            // cookie was set with — a pathless Delete does not match it and the
            // stale cookie survives in the browser.
            var challengeCookie = context.Request.Cookies[PasskeyChallengeCookie];
            context.Response.Cookies.Delete(PasskeyChallengeCookie,
                new CookieOptions { Path = PasskeyCookiePath });

            if (!Guid.TryParse(challengeCookie, out var ceremonyId))
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            var ceremony = await session.LoadAsync<PasskeyCeremony>(ceremonyId, ct);
            if (ceremony is null || ceremony.IsExpired || ceremony.IsConsumed)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            if ((await settingsResolver.ResolveForRequestAsync(context, ceremony.ClientId, ct))
                .LoginExperience?.InternalLoginEnabled == false)
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "LoginExperience.InternalDisabled",
                    detail: "Internal login is disabled for this application.");

            // Single-use: consume before verifying, via a version-checked Store of
            // ConsumedAt (Marten does not version-check deletes), so a captured
            // ceremony can never be replayed and two concurrent logins cannot both
            // redeem one challenge.
            ceremony.ConsumedAt = DateTimeOffset.UtcNow;
            session.Store(ceremony);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (JasperFx.ConcurrencyException)
            {
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            AssertionOptions options;
            try
            {
                options = AssertionOptions.FromJson(ceremony.OptionsJson);
            }
            catch
            {
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            // Verify the assertion via the shared verifier (the SAME FIDO2 verify
            // the native urn:cocoar:passkey grant uses — no fork). Both flows now
            // also share the challenge transport: a server-side ceremony doc. The
            // web flow is realm-scoped: RP ID = PrimaryDomain (the fido2 above was
            // built with it), so legacy null-RpId credentials resolve to it and
            // still verify. Prefer the RP ID pinned at begin-time so an admin
            // changing PrimaryDomain mid-ceremony can't cause a begin/redeem drift.
            var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
            var ceremonyRpId = ceremony.RpId ?? primaryDomain;

            // The browser may run the hosted login on an application subdomain
            // (for example amzettel.auth.example.com) while the realm RP ID is
            // the parent domain (auth.example.com). WebAuthn permits exactly
            // that relationship, but Fido2NetLib still requires the fully
            // qualified signed origin in its allow-list. Mirror the native
            // passkey grant, with an additional same-origin requirement for the
            // hosted web flow: the signed origin must exactly match this request,
            // then RealmFido2 verifies that it equals or is below the pinned RP
            // ID. Foreign, cross-port and malformed origins remain rejected.
            string[]? presentedOrigins = null;
            try
            {
                var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                    body.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin
                    && RealmFido2.IsOriginForRequest(origin, context.Request.Scheme, context.Request.Host))
                    presentedOrigins = [origin];
            }
            catch (JsonException) { /* leave null — the shared verifier fails closed below */ }

            var fido2 = await fido2Factory.CreateAsync(
                ct,
                rpIdOverride: ceremonyRpId,
                additionalOrigins: presentedOrigins);
            var storedCredential = await PasskeyAssertionVerifier.VerifyAsync(
                fido2, options, body.GetRawText(), session, ceremonyRpId, ceremonyRpId, ct);
            if (storedCredential is null)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            // PasskeyAssertionVerifier.VerifyAsync (above) already ran the FIDO2
            // assertion and advanced + persisted the signature counter, so there is
            // no inline MakeAssertionAsync here.
            //
            // Sign in — load via the UserManager finder (NOT a raw session load) so
            // the user is hydrated with authoritative security data (stamp/2FA/
            // lockout) from UserSecurityData rather than a stale ApplicationUser
            // mirror — the SecurityStamp sign-in fix. FindByIdAsync also filters
            // IsDeleted; the explicit IsActive/IsDeleted gate below stays as
            // defense-in-depth (soft-delete auth-bypass).
            var user = await userManager.FindByIdAsync(storedCredential.UserId.ToString());
            if (user is null || !user.IsActive || user.IsDeleted)
            {
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Passkey, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            // Passkey login is always persistent — user can re-authenticate anytime via biometrics
            await signInManager.SignInAsync(user, isPersistent: true);


            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            Serilog.Log.Information("Passkey login successful. UserId={UserId} IP={IP}", user.Id, ip);
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Passkey, ModgudMeters.LoginOutcome.Success);

            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("Passkey_Login")
        .AllowAnonymous();

        return application;
    }
}
