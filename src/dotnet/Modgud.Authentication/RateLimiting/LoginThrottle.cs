using System.Security.Cryptography;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;

using Modgud.Authentication.Devices;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.RateLimiting;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.RateLimiting;

/// <summary>Which failure bucket a login attempt belongs to (ADR 0020).</summary>
public enum LoginBucket
{
    /// <summary>The request carries a device cookie trusted for this user.</summary>
    Device,
    /// <summary>No cookie, or a cookie not trusted for this user: the shared pool.</summary>
    Untrusted,
}

/// <summary>The verdict for one attempt, taken BEFORE the password is checked.</summary>
public sealed record LoginThrottleDecision(
    bool Allowed,
    LoginBucket Bucket,
    Guid? DeviceId,
    bool LogOnly,
    int RetryAfterSeconds,
    RateLimitRule? Rule,
    string Key)
{
    /// <summary>The bucket is exhausted; in enforce mode the attempt is refused, in
    /// log-only mode it would have been.</summary>
    public bool Exhausted => RetryAfterSeconds > 0;
}

/// <summary>What recording a failure changed.</summary>
public sealed record LoginFailureOutcome(bool BucketTripped, bool UnlockDue, bool SprayDetected);

/// <summary>
/// ADR 0020 — the arithmetic behind device-aware login throttling, free of HTTP so
/// it is unit-testable: two failure buckets per user (trusted device / untrusted
/// pool), a permanently silent spray signal per source, and the once-per-window
/// guard for the unlock e-mail. Counters live in the shared
/// <see cref="IRateLimitStore"/>, keys under the <c>login</c> policy.
/// </summary>
public sealed class LoginThrottleCore(IRateLimitStore store, TimeProvider clock)
{
    public static string DeviceKey(Guid deviceId, Guid userId) => $"login|device|{deviceId:N}|{userId:N}";
    public static string UntrustedKey(Guid userId) => $"login|untrusted|{userId:N}";
    public static string SprayKey(string sourceKey) => $"login|spray|{sourceKey}";
    public static string SprayAlertKey(string sourceKey) => $"login|spray-alert|{sourceKey}";
    public static string UnlockKey(Guid userId) => $"login|unlock|{userId:N}";

    public async Task<LoginThrottleDecision> CheckAsync(
        RateLimitScope scope, Guid userId, Guid? deviceId, bool trusted, AuthRateLimitSettings? settings, CancellationToken ct = default)
    {
        var bucket = trusted && deviceId is not null ? LoginBucket.Device : LoginBucket.Untrusted;
        var rule = AuthRateLimitSettings.Effective(settings, AuthRateLimitPolicy.Login,
            bucket == LoginBucket.Device ? RateLimitDimension.Device : RateLimitDimension.Target);
        var key = bucket == LoginBucket.Device ? DeviceKey(deviceId!.Value, userId) : UntrustedKey(userId);
        var logOnly = AuthRateLimitSettings.EffectiveMode(settings) == RateLimitEnforcementMode.LogOnly;

        if (rule is null || !rule.Enabled)
            return new LoginThrottleDecision(true, bucket, deviceId, logOnly, 0, null, key);

        var peek = await store.PeekAsync(scope, key, rule, clock.GetUtcNow(), ct);
        var exhausted = !peek.Allowed;
        return new LoginThrottleDecision(
            Allowed: !exhausted || logOnly,
            bucket, deviceId, logOnly,
            RetryAfterSeconds: exhausted ? peek.RetryAfterSeconds : 0,
            rule, key);
    }

    public async Task<LoginFailureOutcome> RecordFailureAsync(
        RateLimitScope scope, Guid userId, LoginThrottleDecision decision,
        string? sourceKey, bool sourceAllowlisted, AuthRateLimitSettings? settings, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var tripped = false;
        var unlockDue = false;

        if (decision.Rule is { Enabled: true } rule)
        {
            // "Tripped" = this failure filled the bucket (or found it full). The check
            // before the next attempt refuses at "hits >= limit", so the failure that
            // reaches the limit is the one that must trigger the unlock mail — later
            // attempts never get as far as recording a failure.
            var hit = await store.HitAsync(scope, decision.Key, rule, now, ct);
            tripped = !hit.Allowed || !(await store.PeekAsync(scope, decision.Key, rule, now, ct)).Allowed;
            if (tripped && decision.Bucket == LoginBucket.Untrusted)
            {
                // The owner gets ONE unlock mail per window, however many attackers knock.
                var guard = await store.HitAsync(scope, UnlockKey(userId), RateLimitRule.Fixed(1, rule.WindowMinutes), now, ct);
                unlockDue = guard.Allowed;
            }
        }

        // Spray signal: untrusted failures per source. Never blocks — a NAT must not be
        // locked out by one guessing neighbour (decision 2026-05-07); it feeds alerts.
        var spray = false;
        if (decision.Bucket == LoginBucket.Untrusted && sourceKey is not null && !sourceAllowlisted
            && AuthRateLimitSettings.Effective(settings, AuthRateLimitPolicy.Login, RateLimitDimension.Source) is { Enabled: true } sprayRule)
        {
            var hit = await store.HitAsync(scope, SprayKey(sourceKey), sprayRule, now, ct);
            if (!hit.Allowed)
            {
                var alert = await store.HitAsync(scope, SprayAlertKey(sourceKey), RateLimitRule.Fixed(1, sprayRule.WindowMinutes), now, ct);
                spray = alert.Allowed;
            }
        }

        return new LoginFailureOutcome(tripped, unlockDue, spray);
    }
}

/// <summary>HTTP-facing throttle used by the password login endpoint.</summary>
public interface ILoginThrottle
{
    Task<LoginThrottleDecision> CheckAsync(HttpContext http, ApplicationUser user, string? clientId, CancellationToken ct = default);

    /// <summary>Count the failure, raise the spray signal, send the unlock mail when due.
    /// Never throws for mail or audit problems — a failed login must stay a 401.</summary>
    Task RecordFailureAsync(HttpContext http, ApplicationUser user, LoginThrottleDecision decision, string? clientId, CancellationToken ct = default);
}

public sealed class LoginThrottle(
    IRateLimitStore store,
    IDeviceTrust devices,
    IApplicationSettingsResolver settingsResolver,
    IAuthCallerContextFactory callerContext,
    ISecurityAuditLog audit,
    ILoginUnlockMailer unlockMailer,
    TimeProvider clock,
    ILogger<LoginThrottle> logger) : ILoginThrottle
{
    private readonly LoginThrottleCore _core = new(store, clock);

    public async Task<LoginThrottleDecision> CheckAsync(HttpContext http, ApplicationUser user, string? clientId, CancellationToken ct = default)
    {
        var settings = await ResolveSettingsAsync(http, clientId, ct);
        var deviceId = devices.ReadDeviceId(http);
        var trusted = deviceId is { } id && await devices.IsTrustedAsync(id, user.Id, ct);
        var decision = await _core.CheckAsync(Scope(), user.Id, deviceId, trusted, settings, ct);

        if (decision.Exhausted)
        {
            var bucket = decision.Bucket == LoginBucket.Device ? "device" : "untrusted";
            RateLimitMetrics.LoginThrottled(bucket, decision.LogOnly);
            audit.RecordAbuse(new SecurityAuditRecord
            {
                EventType = AuditEvents.LoginThrottled,
                Severity = AuditSeverity.Warning,
                TargetSubjectId = user.Id,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                AuthenticationMethod = ModgudMeters.LoginMethod.Password,
                OutcomeCode = decision.LogOnly ? AuditOutcomes.Observed : AuditOutcomes.Blocked,
                ReasonCode = bucket,
            });
            if (decision.LogOnly)
                logger.LogWarning("Login throttle (log-only): user {UserId} would be refused from the {Bucket} bucket", user.Id, bucket);
        }
        return decision;
    }

    public async Task RecordFailureAsync(HttpContext http, ApplicationUser user, LoginThrottleDecision decision, string? clientId, CancellationToken ct = default)
    {
        try
        {
            var settings = await ResolveSettingsAsync(http, clientId, ct);
            var (sourceKey, allowlisted) = await SourceAsync(http, ct);
            var outcome = await _core.RecordFailureAsync(Scope(), user.Id, decision, sourceKey, allowlisted, settings, ct);

            if (outcome.SprayDetected)
            {
                RateLimitMetrics.LoginSprayDetected();
                audit.RecordAbuse(new SecurityAuditRecord
                {
                    EventType = AuditEvents.LoginSprayDetected,
                    Severity = AuditSeverity.Warning,
                    ActorKind = AuditActorKind.AnonymousIdentifier,
                    IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                    AuthenticationMethod = ModgudMeters.LoginMethod.Password,
                    OutcomeCode = AuditOutcomes.Observed,
                    ReasonCode = "untrusted-failures-per-source",
                    Count = AuthRateLimitSettings.Effective(settings, AuthRateLimitPolicy.Login, RateLimitDimension.Source)?.PermitLimit,
                });
            }

            if (outcome.UnlockDue)
                await unlockMailer.SendAsync(http, user, decision.Rule?.WindowMinutes ?? 15, clientId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Login throttle bookkeeping failed for user {UserId}; the attempt stays refused", user.Id);
        }
    }

    private static RateLimitScope Scope() => new(TenantContext.Current);

    private async Task<AuthRateLimitSettings?> ResolveSettingsAsync(HttpContext http, string? clientId, CancellationToken ct)
    {
        try
        {
            return (await settingsResolver.ResolveForRequestAsync(http, clientId, ct)).AuthRateLimits;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rate-limit settings could not be resolved; login throttle uses defaults");
            return null;
        }
    }

    private async Task<(string? SourceKey, bool Allowlisted)> SourceAsync(HttpContext http, CancellationToken ct)
    {
        var existing = AuthCallerContext.From(http);
        if (existing is not null) return (existing.SourceKey, existing.SourceAllowlisted);
        var built = await callerContext.BuildAsync(http, ct);
        return built.Context is { } c ? (c.SourceKey, c.SourceAllowlisted) : (null, false);
    }
}

/// <summary>ADR 0020 §4 — "sign-in attempts were blocked; if that was you, use this
/// link": a magic-link sign-in that, on success, trusts the device.</summary>
public interface ILoginUnlockMailer
{
    Task SendAsync(HttpContext http, ApplicationUser user, int windowMinutes, string? clientId, CancellationToken ct = default);
}

public sealed class LoginUnlockMailer(
    IDocumentSession session,
    IEmailService email,
    IEmailBrandingResolver branding,
    IMagicLinkConfiguration magicLink,
    IRealmProvisioningService realms,
    ILogger<LoginUnlockMailer> logger) : ILoginUnlockMailer
{
    public async Task SendAsync(HttpContext http, ApplicationUser user, int windowMinutes, string? clientId, CancellationToken ct = default)
    {
        // The link signs the user in, so it needs the same guard as self-service
        // magic links: a verified address and the platform feature enabled.
        if (!magicLink.Enabled || string.IsNullOrEmpty(user.Email) || !user.EmailConfirmed)
        {
            logger.LogInformation("Unlock mail skipped for user {UserId} (magic link disabled or address unverified)", user.Id);
            return;
        }
        var realm = await http.ResolveCurrentRealmAsync(realms, ct);
        if (realm is null) return;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        session.Store(new MagicLinkChallenge
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = MagicLinkChallenge.HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(magicLink.ExpirationMinutes),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);

        var url = $"{RealmPublicUrl.RealmPublicBaseUrl(realm)}/magic-login?userId={user.Id}&token={Uri.EscapeDataString(token)}";
        await email.SendTemplatedEmailAsync(
            user.Email,
            EmailTemplate.LoginBlocked,
            await branding.ApplyAsync(new Dictionary<string, string>
            {
                ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                ["ActionUrl"] = url,
                ["ExpirationMinutes"] = magicLink.ExpirationMinutes.ToString(),
                ["WindowMinutes"] = windowMinutes.ToString(),
            }, clientId: clientId, ct: ct), ct);
        logger.LogInformation("Unlock mail sent to user {UserId}", user.Id);
    }
}
