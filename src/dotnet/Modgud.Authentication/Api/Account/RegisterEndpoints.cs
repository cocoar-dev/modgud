using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.SelfRegistration;
using Modgud.Authentication.SelfRegistration.Captcha;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Marten;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// Public self-registration endpoints. All anonymous — the realm
/// resolves via the Host header / <c>RealmMiddleware</c> like the other
/// /api/account/* endpoints. Per-realm self-reg config gates everything.
///
/// <para>Routes:</para>
/// <list type="bullet">
///   <item><c>GET /api/account/self-registration-info</c> — public-shape
///   config the SPA reads before mounting /register</item>
///   <item><c>POST /api/account/register</c> — submit the registration
///   form. Anti-enumeration: same 200-OK shape regardless of outcome.</item>
///   <item><c>POST /api/account/register/verify-email</c> — consume the
///   email-verification magic-link token. Surfaces real errors
///   (expired / used / unknown) because by this point the user is
///   already in possession of the token; nothing to enumerate.</item>
/// </list>
/// </summary>
public static class RegisterEndpoints
{
    public static WebApplication MapRegisterEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/account");

        group.MapGet("self-registration-info", async (
            HttpContext http,
            Modgud.Authentication.Applications.IApplicationSettingsResolver settingsResolver,
            ITurnstileSecretResolver resolver,
            CancellationToken ct) =>
        {
            // ADR-0011 — effective (App ⊕ realm) self-registration, Host-resolved:
            // on an Application subdomain the SPA gets the App's self-reg posture.
            var settings = (await settingsResolver.ResolveForRequestAsync(http, clientId: null, ct)).SelfRegistration;

            // Anti-enumeration: always return SOMETHING. A drive-by can't
            // tell whether a realm has self-reg disabled or just isn't
            // configured. When Enabled=false, the rest of the fields
            // mirror SelfRegistrationSettings defaults (mostly false /
            // null) — the SPA reads Enabled and either redirects to
            // /login or renders the form.
            if (settings is null || !settings.Enabled)
                return Results.Ok(new SelfRegistrationInfoDto());

            return Results.Ok(new SelfRegistrationInfoDto
            {
                Enabled = true,
                RequireEmailVerification = settings.RequireEmailVerification,
                RequireAdminApproval = settings.RequireAdminApproval,
                AllowedEmailDomains = settings.AllowedEmailDomains,
                TermsOfServiceUrl = settings.TermsOfServiceUrl,
                PrivacyPolicyUrl = settings.PrivacyPolicyUrl,
                // Site-key only when captcha is actually enabled. We
                // resolve through the same fallback chain the verifier
                // uses — per-realm override → cocoar-default — so the
                // SPA mounts the right widget without knowing which
                // source the key came from.
                CaptchaSiteKey = settings.CaptchaEnabled ? resolver.ResolveSiteKey(settings) : null,
            });
        })
        .WithName("Account_SelfRegistrationInfo")
        .AllowAnonymous();

        group.MapPost("register", async (
            RegisterDto dto,
            HttpContext http,
            IRealmProvisioningService realmSvc,
            ISelfRegistrationService selfReg,
            CancellationToken ct) =>
        {
            var realm = await ResolveCurrentRealmAsync(http, realmSvc, ct);
            if (realm is null)
            {
                // No resolvable realm = the Host header didn't match a
                // registered tenant. RealmMiddleware would have 404'd
                // before reaching us; if we get here something exotic is
                // going on. Generic response to keep anti-enumeration.
                return Results.Ok(new RegisterResponseDto { Message = "OK." });
            }

            var remoteIp = http.Connection.RemoteIpAddress?.ToString();
            var response = await selfReg.RegisterAsync(realm, dto, remoteIp, ct);
            return Results.Ok(response);
        })
        .WithName("Account_Register")
        .AllowAnonymous();

        group.MapPost("register/verify-email", async (
            VerifyEmailDto dto,
            ISelfRegistrationService selfReg,
            CancellationToken ct) =>
        {
            var result = await selfReg.VerifyEmailAsync(dto.Token, ct);
            return result.ToResult(payload => Results.Ok(new VerifyEmailResponseDto
            {
                UserName = payload.UserName,
                Email = payload.Email,
                RequiresAdminApproval = payload.RequiresAdminApproval,
            }));
        })
        .WithName("Account_VerifyEmail")
        .AllowAnonymous();

        return app;
    }

    private static async Task<Realm?> ResolveCurrentRealmAsync(
        HttpContext http,
        IRealmProvisioningService realmSvc,
        CancellationToken ct)
    {
        var tenantId = http.Items[TenantConstants.HttpContextTenantIdKey] as string;
        if (string.IsNullOrEmpty(tenantId)) return null;
        return await realmSvc.GetRealmBySlugAsync(tenantId, ct);
    }
}

public record VerifyEmailDto
{
    public string Token { get; init; } = string.Empty;
}

public record VerifyEmailResponseDto
{
    public required string UserName { get; init; }
    public required string Email { get; init; }
    public bool RequiresAdminApproval { get; init; }
}
