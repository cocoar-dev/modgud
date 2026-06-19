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

namespace Modgud.Authentication.Api.Account;

public static class PasskeyEndpoints
{
    public record PasskeyLoginOptionsRequest(string? UserName = null);
    public record PasskeyDisplayDto(string Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    private const string RegistrationCacheKey = "fido2.attestationOptions";

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
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            // RP = current realm's primary domain. Built per request so the
            // same RP is used to create and (later) verify the credential.
            var fido2 = await fido2Factory.CreateAsync(ct);

            var existingCredentials = await session.Query<StoredPasskeyCredential>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync();

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
                AuthenticatorSelection = AuthenticatorSelection.Default,
                AttestationPreference = AttestationConveyancePreference.None,
            });

            var json = options.ToJson();
            context.Session.SetString(RegistrationCacheKey, json);

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

            var fido2 = await fido2Factory.CreateAsync(ct);

            var optionsJson = context.Session.GetString(RegistrationCacheKey);
            if (string.IsNullOrEmpty(optionsJson))
                return Results.BadRequest(new { Message = "Registration session expired. Please try again." });

            var options = CredentialCreateOptions.FromJson(optionsJson);
            context.Session.Remove(RegistrationCacheKey);

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
            IDocumentSession session) =>
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

            return Results.Ok(new { SecureSetupRequired = secureSetupRequired });
        })
        .WithName("Passkey_Delete");

        // ═══ Login (anonymous) ═══

        // POST /api/account/passkey/login-options — Generate assertion options
        group.MapPost("login-options", async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            IDocumentSession session,
            PasskeyLoginOptionsRequest? request,
            CancellationToken ct) =>
        {
            var fido2 = await fido2Factory.CreateAsync(ct);

            List<PublicKeyCredentialDescriptor>? allowedCredentials = null;

            if (!string.IsNullOrEmpty(request?.UserName))
            {
                var user = await session.Query<ApplicationUser>()
                    .FirstOrDefaultAsync(u => u.NormalizedUserName == request.UserName.ToUpperInvariant());

                if (user is not null)
                {
                    var credentials = await session.Query<StoredPasskeyCredential>()
                        .Where(c => c.UserId == user.Id)
                        .ToListAsync();

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

            // Store challenge in a secure cookie (anonymous users don't have sessions).
            // Secure=Request.IsHttps mirrors CookieSecurePolicy.SameAsRequest:
            // the cookie carries Secure on HTTPS requests and not on plain HTTP
            // (dev only). With ForwardedHeaders middleware behind the reverse
            // proxy, IsHttps reflects the public scheme, so production deploys
            // always get Secure even when Kestrel itself listens on HTTP behind
            // the proxy.
            var optionsJson = options.ToJson();
            context.Response.Cookies.Append("Modgud.Passkey.Challenge",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(optionsJson)),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromMinutes(5),
                    Path = "/api/account/passkey",
                });

            return Results.Content(optionsJson, "application/json");
        })
        .WithName("Passkey_LoginOptions")
        .AllowAnonymous();

        // POST /api/account/passkey/login — Verify assertion and sign in
        group.MapPost("login", async (
            HttpContext context,
            RealmScopedFido2Factory fido2Factory,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ISessionService sessionService,
            JsonElement body,
            CancellationToken ct) =>
        {
            var fido2 = await fido2Factory.CreateAsync(ct);

            // Retrieve challenge from cookie
            var challengeCookie = context.Request.Cookies["Modgud.Passkey.Challenge"];
            if (string.IsNullOrEmpty(challengeCookie))
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            AssertionOptions options;
            try
            {
                var optionsJson = Encoding.UTF8.GetString(Convert.FromBase64String(challengeCookie));
                options = AssertionOptions.FromJson(optionsJson);
            }
            catch
            {
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            context.Response.Cookies.Delete("Modgud.Passkey.Challenge");

            var fido2JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(body.GetRawText(), fido2JsonOptions);
            if (assertionResponse is null)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            // Find stored credential by credential ID
            var assertionCredentialId = Convert.FromBase64String(
                assertionResponse.Id.Replace('-', '+').Replace('_', '/').PadRight(
                    assertionResponse.Id.Length + (4 - assertionResponse.Id.Length % 4) % 4, '='));
            var allCredentials = await session.Query<StoredPasskeyCredential>().ToListAsync();
            var storedCredential = allCredentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(assertionCredentialId));

            if (storedCredential is null)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = options,
                StoredPublicKey = storedCredential.PublicKey,
                StoredSignatureCounter = storedCredential.SignatureCount,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, ct) =>
                {
                    var credential = allCredentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(args.CredentialId));
                    return credential?.UserHandle.SequenceEqual(args.UserHandle) ?? false;
                },
            });

            // Update signature counter
            storedCredential.SignatureCount = result.SignCount;
            storedCredential.LastUsedAt = DateTimeOffset.UtcNow;
            session.Store(storedCredential);
            await session.SaveChangesAsync();

            // Sign in — load via the UserManager finder so the user is hydrated
            // with authoritative security data (stamp/2FA/lockout) from
            // UserSecurityData rather than a stale ApplicationUser mirror.
            // FindByIdAsync also filters IsDeleted; the explicit IsActive/IsDeleted
            // gate below stays as defense-in-depth (soft-delete auth-bypass).
            var user = await userManager.FindByIdAsync(storedCredential.UserId.ToString());
            if (user is null || !user.IsActive || user.IsDeleted)
            {
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.Passkey, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);
            }

            // Passkey login is always persistent — user can re-authenticate anytime via biometrics
            await signInManager.SignInAsync(user, isPersistent: true);

            await SessionTracker.RecordLoginAsync(sessionService, context, user.Id);

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
