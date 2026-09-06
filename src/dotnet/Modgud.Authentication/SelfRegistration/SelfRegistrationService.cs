using System.Security.Cryptography;
using System.Text;
using Modgud.Application.DTOs.SelfRegistration;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.SelfRegistration.Captcha;
using Modgud.Authentication.SelfRegistration.Domain;
using Modgud.Authorization.Principals;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.SelfRegistration;

/// <summary>
/// Orchestrates the public self-registration flow:
/// <list type="bullet">
///   <item><c>RegisterAsync</c> — validate, captcha-verify, anti-enumerate,
///   hash the password and hand the sign-up to the registration pipeline
///   (ADR 0018: pending record + verification link; NO user until proved).</item>
///   <item><c>VerifyEmailAsync</c> — prove the link: the pipeline creates the
///   confirmed user, attaches the snapshotted default groups and respects
///   RequireAdminApproval. Legacy pre-pipeline rows still consume for one release.</item>
/// </list>
///
/// <para>Tenant-scoped: the <see cref="IDocumentSession"/> + UserManager
/// reach into the current realm's DB via the standard middleware
/// resolution. Caller is expected to ensure RealmMiddleware has already
/// set <c>HttpContext.Items["TenantId"]</c>.</para>
/// </summary>
public interface ISelfRegistrationService
{
    Task<RegisterResponseDto> RegisterAsync(
        Realm realm,
        RegisterDto dto,
        string? remoteIp,
        CancellationToken ct);

    Task<ErrorOr<VerifyEmailResult>> VerifyEmailAsync(
        string plaintextToken,
        CancellationToken ct);
}

/// <summary>Outcome of a successful verify-email consume. Caller can
/// decide whether to auto-sign-in or just return a confirmation.</summary>
public sealed record VerifyEmailResult(
    Guid UserId,
    string UserName,
    string Email,
    bool RequiresAdminApproval);

public sealed class SelfRegistrationService(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    TurnstileVerifier captchaVerifier,
    Modgud.Authentication.Registration.IRegistrationPipeline registrationPipeline,
    IHostEnvironment env,
    Modgud.Authentication.Applications.IApplicationSettingsResolver settingsResolver,
    Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor,
    ILogger<SelfRegistrationService> logger) : ISelfRegistrationService
{
    // Single response shape — anti-enumeration. Don't leak whether an
    // email was already taken, whether a captcha-token was bad, whether
    // rate-limit kicked in. The user sees the same "if your registration
    // was valid, you'll get an email" regardless.
    private static readonly RegisterResponseDto GenericSuccess = new()
    {
        Message = "Falls die Registrierung gültig ist, kommt eine E-Mail mit dem Bestätigungslink.",
    };

    public async Task<RegisterResponseDto> RegisterAsync(
        Realm realm,
        RegisterDto dto,
        string? remoteIp,
        CancellationToken ct)
    {
        // ADR-0011 — effective (App ⊕ realm) self-registration: on an Application
        // subdomain the App's posture/policy overrides the realm's; on a plain
        // tenant host this is the realm setting unchanged.
        var clientId = Modgud.Authentication.Api.ExternalAuth.ExternalAuthEndpoints
            .ExtractAuthorizeClientId(dto.ReturnUrl);
        var http = httpContextAccessor.HttpContext;
        var effective = http is null
            ? await settingsResolver.ResolveAsync(applicationId: null, ct)
            : await settingsResolver.ResolveForRequestAsync(http, clientId, ct);
        var settings = effective.SelfRegistration;
        var registrationFields = effective.RegistrationFields ?? RegistrationFieldsSettings.Defaults;
        if (settings is null || !settings.Enabled)
        {
            // Anti-enumeration: same response shape even if the feature
            // is off. A drive-by tester can't tell that this realm
            // doesn't allow public registration vs. just doesn't have
            // their email registered.
            return GenericSuccess;
        }

        // Honeypot — bots fill this, humans never see it. Silently drop.
        if (!string.IsNullOrEmpty(dto.Honeypot))
        {
            logger.LogInformation("Self-reg: honeypot trigger from realm={Realm}", realm.Slug);
            return GenericSuccess;
        }

        // Required-fields gate (without leaking which one failed). Email + password
        // are always required; username + names follow the configurable (App⊕realm)
        // policy. A missing required field is a silent generic response, consistent
        // with the rest of this anti-enumeration flow.
        if (string.IsNullOrWhiteSpace(dto.Email)
            || string.IsNullOrWhiteSpace(dto.Password)
            || !dto.Email.Contains('@'))
        {
            return GenericSuccess;
        }
        if (RegistrationFieldsPolicy.FirstMissingRequired(
                registrationFields, dto.UserName, dto.Firstname, dto.Lastname) is not null)
        {
            return GenericSuccess;
        }

        // Terms-of-Service: only enforced when a ToS URL is configured.
        if (!string.IsNullOrEmpty(settings.TermsOfServiceUrl) && !dto.AcceptedTerms)
        {
            return GenericSuccess;
        }

        // Email-domain allow-list — quiet rejection.
        if (settings.AllowedEmailDomains is { Length: > 0 } allowed)
        {
            var atIdx = dto.Email.LastIndexOf('@');
            var domain = atIdx >= 0 ? dto.Email[(atIdx + 1)..] : "";
            if (!allowed.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
                return GenericSuccess;
        }

        // Per-address / per-source ceilings are the endpoint's rate-limit policy
        // (ADR 0019, self-registration) plus the pipeline's cooldown — no in-memory
        // limiter here any more.
        var normalizedEmail = dto.Email.Trim();

        // Captcha verify — Skipped/Verified is fine, Failed means the
        // submit didn't carry a valid token. Generic response either way.
        var captchaResult = await captchaVerifier.VerifyAsync(settings, dto.CaptchaToken, remoteIp, ct);
        if (captchaResult == CaptchaResult.Failed) return GenericSuccess;

        // Anti-enumeration on email: check uniqueness via Person query
        // (matches the admin user-create path's check). If taken, we
        // STILL return the same success message — no email is sent so
        // the original account holder isn't bothered.
        var emailTaken = await session.Query<Person>()
            .AnyAsync(p => p.NormalizedEmail == normalizedEmail.ToUpperInvariant() && !p.IsDeleted, ct);
        if (emailTaken) return GenericSuccess;

        // Username collision IS surfaced — usernames are public-shape
        // identifiers (vs. emails which are PII). But we still keep the
        // surface uniform: same success message, just no record created
        // and no email sent. The SPA-side validation should surface the
        // "username taken" error pre-submit; this is the second line of
        // defense.
        // Resolve the username per policy: Off → the email; Optional/blank → the
        // email; else the supplied username (validated non-empty above when Required).
        var normalizedUserName = RegistrationFieldsPolicy
            .ResolveUsername(registrationFields, dto.UserName, normalizedEmail)
            .ToLowerInvariant();
        var userNameTaken = await session.Query<Person>()
            .AnyAsync(p => p.AccountName == normalizedUserName && !p.IsDeleted, ct);
        if (userNameTaken) return GenericSuccess;

        // MG-FT-01 — position principals share the account-name namespace; a
        // self-registration must not take a position's handle. Same uniform
        // GenericSuccess so the check can't be used for name enumeration.
        var positionNameTaken = await session.Query<PositionPrincipal>()
            .AnyAsync(f => f.AccountName == normalizedUserName && !f.IsDeleted, ct);
        if (positionNameTaken) return GenericSuccess;

        // ADR 0018 — validate + hash the password NOW; the user itself is materialised
        // only when the verification link is proved. Until then the sign-up is a
        // pending record keyed by the address (one per address, hard-deleted on
        // proof/expiry), so a stranger's attempt can never occupy someone's address.
        var probe = new ApplicationUser(normalizedUserName, normalizedEmail);
        foreach (var validator in userManager.PasswordValidators)
        {
            var check = await validator.ValidateAsync(userManager, probe, dto.Password);
            if (!check.Succeeded)
            {
                logger.LogInformation(
                    "Self-reg: password rejected, realm={Realm} errors={Errors}",
                    realm.Slug, string.Join(';', check.Errors.Select(e => e.Code)));
                return GenericSuccess;
            }
        }
        var passwordHash = userManager.PasswordHasher.HashPassword(probe, dto.Password);

        // Thread the pending continuation through the e-mail round trip so a
        // self-registration can resume a client app's OIDC authorize flow.
        // Same-origin guard mirrors the SPA's; the verify page forwards
        // ?redirect= to /login once the account is confirmed.
        var returnUrl = Modgud.Authentication.Api.LoginRedirectGuard.IsSameOriginPath(dto.ReturnUrl) && dto.ReturnUrl != "/"
            ? dto.ReturnUrl
            : null;

        var request = new Modgud.Authentication.Registration.RegistrationRequest(
            Email: normalizedEmail,
            UserName: normalizedUserName,
            Firstname: dto.Firstname,
            Lastname: dto.Lastname,
            PasswordHash: passwordHash,
            ProofKind: Modgud.Authentication.Registration.RegistrationProofKind.Link,
            Source: Modgud.Authentication.Registration.RegistrationSources.Web,
            ApplicationId: http is null
                ? null
                : Modgud.Infrastructure.Persistence.Tenancy.HttpContextApplicationExtensions.GetApplicationId(http),
            ClientId: clientId,
            ReturnUrl: returnUrl,
            LinkBaseUrl: RealmPublicUrl.RealmPublicBaseUrl(realm),
            DefaultGroupIds: settings.DefaultGroupIds ?? [],
            RequireAdminApproval: settings.RequireAdminApproval);

        if (settings.RequireEmailVerification)
        {
            var outcome = await registrationPipeline.RequestAsync(request, ct);
            logger.LogInformation("Self-reg: pending registration {Outcome}, realm={Realm}", outcome, realm.Slug);
        }
        else
        {
            // The realm explicitly opted out of proof: the user is created right away,
            // confirmed, default groups attached; admin approval still gates activation.
            var created = await registrationPipeline.RegisterWithoutProofAsync(request, ct);
            if (created.IsError)
                logger.LogInformation(
                    "Self-reg: immediate registration refused ({Code}), realm={Realm}",
                    created.FirstError.Code, realm.Slug);
            else
                logger.LogInformation(
                    "Self-reg: RequireEmailVerification=false, created confirmed user {UserId} in realm={Realm}",
                    created.Value.User.Id, realm.Slug);
        }

        return GenericSuccess;
    }

    public async Task<ErrorOr<VerifyEmailResult>> VerifyEmailAsync(
        string plaintextToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            return Error.Validation("SelfRegistration.TokenRequired", "Verification token is required.");

        // ADR 0018 — the pending pipeline owns every new registration.
        var proved = await registrationPipeline.ProveLinkAsync(plaintextToken, ct);
        if (!proved.IsError)
        {
            var created = proved.Value;
            return new VerifyEmailResult(
                UserId: created.User.Id,
                UserName: created.User.UserName ?? "",
                Email: created.User.Email ?? "",
                RequiresAdminApproval: created.RequiresAdminApproval);
        }
        switch (proved.FirstError.Code)
        {
            case Modgud.Authentication.Registration.RegistrationPipeline.ErrorExpired:
                return Error.Validation("SelfRegistration.TokenExpired", "Verification token has expired.");
            case Modgud.Authentication.Registration.RegistrationPipeline.ErrorAlreadyConsumed:
                return Error.Validation("SelfRegistration.TokenUsed", "Verification token has already been used.");
            case Modgud.Authentication.Registration.RegistrationPipeline.ErrorRejected:
                return Error.Validation("SelfRegistration.Rejected", proved.FirstError.Description);
            case Modgud.Authentication.Registration.RegistrationPipeline.ErrorNoPendingProof:
                break; // not a pipeline token → try the legacy rows below
            default:
                return proved.Errors;
        }

        // Legacy: rows written by the pre-ADR-0018 flow (user created first, token
        // row keyed by UserId). Kept for one release so in-flight verifications
        // still complete; remove together with PendingSelfRegistration.
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));

        var pending = await session.Query<PendingSelfRegistration>()
            .FirstOrDefaultAsync(p => p.TokenHash == tokenHash, ct);
        if (pending is null)
            return Error.Validation("SelfRegistration.TokenUnknown", "Verification token is invalid.");
        if (pending.IsUsed)
            return Error.Validation("SelfRegistration.TokenUsed", "Verification token has already been used.");
        if (pending.IsExpired)
            return Error.Validation("SelfRegistration.TokenExpired", "Verification token has expired.");

        return await ConsumeAsync(pending, ct);
    }

    private async Task<VerifyEmailResult> ConsumeAsync(PendingSelfRegistration pending, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(pending.UserId.ToString())
                   ?? throw new InvalidOperationException(
                       $"Pending self-registration {pending.Id} references missing user {pending.UserId}.");

        // Mark Identity-side email-confirmed. Pending users (admin-
        // approval-required) STAY IsActive=false; activation moves to
        // the admin-approve endpoint.
        user.EmailConfirmed = true;
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Identity refused to confirm email: "
                + string.Join(';', updateResult.Errors.Select(e => e.Description)));
        }

        // Attach default groups (snapshot from register time so admin
        // changes between register and verify don't change the contract).
        if (pending.DefaultGroupIds is { Length: > 0 })
        {
            foreach (var gidStr in pending.DefaultGroupIds)
            {
                if (!Guid.TryParse(gidStr, out var gid)) continue;
                var group = await session.LoadAsync<Group>(gid, ct);
                if (group is null || group.IsDeleted) continue;
                if (!group.MemberIds.Contains(pending.UserId))
                {
                    group.MemberIds.Add(pending.UserId);
                    session.Store(group);
                }
            }
        }

        pending.UsedAt = DateTimeOffset.UtcNow;
        session.Store(pending);
        await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "Self-reg: verified+activated user {UserId} (approval-required={Approval})",
            pending.UserId, pending.RequireAdminApproval);

        return new VerifyEmailResult(
            UserId: pending.UserId,
            UserName: user.UserName ?? "",
            Email: pending.Email,
            RequiresAdminApproval: pending.RequireAdminApproval);
    }
}
