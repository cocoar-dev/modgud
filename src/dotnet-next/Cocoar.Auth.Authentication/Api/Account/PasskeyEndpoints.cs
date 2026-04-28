using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Authentication.Api.Account;

public static class PasskeyEndpoints
{
    public record PasskeyLoginOptionsRequest(string? UserName = null);
    public record PasskeyDisplayDto(string Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    private const string RegistrationCacheKey = "fido2.attestationOptions";

    public static WebApplication MapPasskeyEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/passkey")
            .WithTags("Passkey / WebAuthn");

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
            IFido2 fido2,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

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
            IFido2 fido2,
            IDocumentSession session,
            JsonElement body) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

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
                    var existing = await session.Query<StoredPasskeyCredential>()
                        .AnyAsync(c => c.CredentialId == args.CredentialId, ct);
                    return !existing;
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
            IFido2 fido2,
            IDocumentSession session,
            PasskeyLoginOptionsRequest? request) =>
        {
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
                UserVerification = UserVerificationRequirement.Preferred,
            });

            // Store challenge in a secure cookie (anonymous users don't have sessions)
            var optionsJson = options.ToJson();
            context.Response.Cookies.Append("Cocoar.Auth.Passkey.Challenge",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(optionsJson)),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = !context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsProduction()
                        ? false : true,
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
            IFido2 fido2,
            IDocumentSession session,
            SignInManager<ApplicationUser> signInManager,
            JsonElement body) =>
        {
            // Retrieve challenge from cookie
            var challengeCookie = context.Request.Cookies["Cocoar.Auth.Passkey.Challenge"];
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

            context.Response.Cookies.Delete("Cocoar.Auth.Passkey.Challenge");

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

            // Sign in
            var user = await session.LoadAsync<ApplicationUser>(storedCredential.UserId);
            if (user is null || !user.IsActive)
                return Results.Json(new { Message = "Invalid credentials" }, statusCode: 401);

            // Passkey login is always persistent — user can re-authenticate anytime via biometrics
            await signInManager.SignInAsync(user, isPersistent: true);

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            Serilog.Log.Information("Auth: Passkey login successful. User={UserName} IP={IP}", user.UserName, ip);

            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("Passkey_Login")
        .AllowAnonymous();

        return application;
    }
}
