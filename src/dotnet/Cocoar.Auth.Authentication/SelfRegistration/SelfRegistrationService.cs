using System.Security.Cryptography;
using System.Text;
using Cocoar.Auth.Application.DTOs.SelfRegistration;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.SelfRegistration.Captcha;
using Cocoar.Auth.Authentication.SelfRegistration.Domain;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Email;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RealmSettingsDoc = Cocoar.Auth.Domain.RealmSettings.RealmSettings;

namespace Cocoar.Auth.Authentication.SelfRegistration;

/// <summary>
/// Orchestrates the public self-registration flow:
/// <list type="bullet">
///   <item><c>RegisterAsync</c> — validate, captcha-verify,
///   anti-enumerate, create user (Identity), issue verification-token,
///   send email.</item>
///   <item><c>VerifyEmailAsync</c> — consume token, mark
///   EmailConfirmed, attach default-groups (snapshotted at register
///   time), respect RequireAdminApproval.</item>
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
    RegistrationRateLimiter rateLimiter,
    IEmailService emailService,
    IServerConfiguration serverConf,
    IHostEnvironment env,
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
        // Tenant-scoped session points at the current realm's DB (resolved
        // by RealmMiddleware). The settings doc lives there as a singleton.
        var settingsDoc = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var settings = settingsDoc?.SelfRegistration;
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

        // Required-fields gate (without leaking which one failed).
        if (string.IsNullOrWhiteSpace(dto.UserName)
            || string.IsNullOrWhiteSpace(dto.Email)
            || string.IsNullOrWhiteSpace(dto.Password)
            || !dto.Email.Contains('@'))
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

        // Rate-limit per email. Same response on rate-limit so bots
        // can't enumerate-by-throttle either.
        var normalizedEmail = dto.Email.Trim();
        if (!rateLimiter.TryConsume(normalizedEmail))
        {
            logger.LogInformation(
                "Self-reg: rate-limit consumed, realm={Realm} email={Email}",
                realm.Slug, LogPiiMasking.MaskEmail(normalizedEmail));
            return GenericSuccess;
        }

        // Captcha verify — Skipped/Verified is fine, Failed means the
        // submit didn't carry a valid token. Generic response either way.
        var captchaResult = await captchaVerifier.VerifyAsync(settings, dto.CaptchaToken, remoteIp, ct);
        if (captchaResult == CaptchaResult.Failed) return GenericSuccess;

        // Anti-enumeration on email: check uniqueness via Person query
        // (matches the admin user-create path's check). If taken, we
        // STILL return the same success message — no email is sent so
        // the original account holder isn't bothered.
        var emailTaken = await session.Query<Person>()
            .AnyAsync(p => p.Email == normalizedEmail && !p.IsDeleted, ct);
        if (emailTaken) return GenericSuccess;

        // Username collision IS surfaced — usernames are public-shape
        // identifiers (vs. emails which are PII). But we still keep the
        // surface uniform: same success message, just no record created
        // and no email sent. The SPA-side validation should surface the
        // "username taken" error pre-submit; this is the second line of
        // defense.
        var normalizedUserName = dto.UserName.Trim().ToLowerInvariant();
        var userNameTaken = await session.Query<Person>()
            .AnyAsync(p => p.AccountName == normalizedUserName && !p.IsDeleted, ct);
        if (userNameTaken) return GenericSuccess;

        // Create the Identity user. EmailConfirmed=false; IsActive
        // depends on RequireAdminApproval (pending users are inactive
        // until an admin flips the flag from the admin UI).
        var appUser = new ApplicationUser(normalizedUserName, normalizedEmail)
        {
            Id = Guid.NewGuid(),
            Firstname = dto.Firstname,
            Lastname = dto.Lastname,
            IsActive = !settings.RequireAdminApproval,
        };
        var createResult = await userManager.CreateAsync(appUser, dto.Password);
        if (!createResult.Succeeded)
        {
            logger.LogInformation(
                "Self-reg: Identity rejected user creation, realm={Realm} errors={Errors}",
                realm.Slug,
                string.Join(';', createResult.Errors.Select(e => e.Code)));
            return GenericSuccess;
        }

        // Issue verification token + magic-link URL. Mirrors
        // PendingAdminInvite's shape: 32-byte base64url plaintext,
        // SHA-256-hex stored.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var pending = new PendingSelfRegistration
        {
            Id = Guid.NewGuid(),
            UserId = appUser.Id,
            Email = normalizedEmail,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(PendingSelfRegistration.DefaultExpirationHours),
            CreatedAt = DateTimeOffset.UtcNow,
            DefaultGroupIds = settings.DefaultGroupIds ?? [],
            RequireAdminApproval = settings.RequireAdminApproval,
        };
        session.Store(pending);
        await session.SaveChangesAsync(ct);

        // Skip email entirely when the realm explicitly opts out of
        // verification (RequireEmailVerification=false). Edge case — for
        // most setups verification is mandatory. The user record is
        // already EmailConfirmed=false; the verify-email consume is what
        // flips it. We log so the admin sees why no email was sent.
        if (settings.RequireEmailVerification)
        {
            await SendVerificationEmailAsync(appUser, realm, token, ct);
        }
        else
        {
            // Treat as immediate verification: trigger the same path the
            // user would walk by clicking the link, so groups land and
            // approval still gates.
            await ConsumeAsync(pending, ct);
            logger.LogInformation(
                "Self-reg: RequireEmailVerification=false, auto-confirmed user {UserId} in realm={Realm}",
                appUser.Id, realm.Slug);
        }

        return GenericSuccess;
    }

    public async Task<ErrorOr<VerifyEmailResult>> VerifyEmailAsync(
        string plaintextToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            return Error.Validation("SelfRegistration.TokenRequired", "Verification token is required.");

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

    private async Task SendVerificationEmailAsync(
        ApplicationUser user,
        Realm realm,
        string plaintextToken,
        CancellationToken ct)
    {
        var appUrl = (serverConf.PublicUrl ?? (env.IsDevelopment() ? "http://localhost:4300" : serverConf.AppUrl)).TrimEnd('/');
        var url = $"{appUrl}/verify-email?token={Uri.EscapeDataString(plaintextToken)}";

        var displayName = !string.IsNullOrWhiteSpace(user.Firstname)
            ? $"{user.Firstname} {user.Lastname}".Trim()
            : user.UserName ?? user.Email ?? "";

        try
        {
            await emailService.SendTemplatedEmailAsync(
                user.Email!,
                EmailTemplate.EmailVerification,
                new Dictionary<string, string>
                {
                    ["AppName"] = realm.DisplayName,
                    ["DisplayName"] = displayName,
                    ["ActionUrl"] = url,
                    ["ExpirationHours"] = PendingSelfRegistration.DefaultExpirationHours.ToString(),
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Self-reg: verification email delivery failed, realm={Realm} email={MaskedEmail}",
                realm.Slug, LogPiiMasking.MaskEmail(user.Email!));
        }
    }
}
