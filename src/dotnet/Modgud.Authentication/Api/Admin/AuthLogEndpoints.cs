using Marten;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Two deliberately separate log surfaces:
/// - /auth-log reads only the caller realm's physical database.
/// - /platform-audit reads only the PII-free Global Store and is Control-Plane only.
/// Neither surface offers arbitrary deletion; retention jobs are the only delete path.
/// </summary>
public static class AuthLogEndpoints
{
    public static WebApplication MapAuthLogEndpoints(this WebApplication application, string path)
    {
        MapRealmSecurityLog(application, path);
        MapPlatformAuditLog(application, path);
        return application;
    }

    private static void MapRealmSecurityLog(WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/auth-log")
            .WithTags("Admin Security Log")
            .RequireAuthorization();

        group.MapGet("", async (
            IDocumentSession session,
            string? category,
            string? eventType,
            int? limit,
            CancellationToken ct) =>
        {
            IQueryable<RealmSecurityAuditEvent> query = session.Query<RealmSecurityAuditEvent>();
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(x => x.EventType == eventType);

            var rows = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync(ct);

            var subjectIds = rows
                .SelectMany(x => new[] { x.ActorSubjectId, x.TargetSubjectId })
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray();

            var users = subjectIds.Length == 0
                ? []
                : await session.Query<UserView>()
                    .Where(x => subjectIds.Contains(x.Id))
                    .ToListAsync(ct);
            var names = users.ToDictionary(x => x.Id, x => x.GetDisplayLabel());

            return Results.Ok(rows.Select(row => RealmSecurityLogDto.From(row, names)));
        })
        .WithName("AdminAuthLog_Get")
        .RequiresPermission("auth-log:read");
    }

    private static void MapPlatformAuditLog(WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/platform-audit")
            .WithTags("Platform Audit Log")
            .RequireAuthorization()
            .AddEndpointFilter<RequireControlPlaneFilter>();

        group.MapGet("", async (
            IGlobalStore store,
            string? category,
            string? eventType,
            int? limit,
            CancellationToken ct) =>
        {
            await using var session = store.QuerySession();
            IQueryable<PlatformAuditEvent> query = session.Query<PlatformAuditEvent>();
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(x => x.EventType == eventType);

            var rows = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(rows.Select(PlatformAuditLogDto.From));
        })
        .WithName("AdminPlatformAudit_Get")
        .RequiresPermission("platform-audit:read", AppSlugs.ControlPlane);
    }
}

public sealed record RealmSecurityLogDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string Category,
    string EventType,
    string Severity,
    string ActorKind,
    string Actor,
    string? Target,
    string? IpAddress,
    string? UserAgent,
    string? OAuthClientId,
    Guid? ApplicationId,
    Guid? SessionId,
    Guid? LoginProviderId,
    string? AuthenticationMethod,
    string? CorrelationId,
    string OutcomeCode,
    string? ReasonCode,
    string? OperationCode,
    string? TargetRealmSlug,
    string? KeyId,
    int? Count,
    int? RelatedCount,
    int? RetentionDays,
    DateTimeOffset? EffectiveAt,
    string Message)
{
    internal static RealmSecurityLogDto From(
        RealmSecurityAuditEvent row,
        IReadOnlyDictionary<Guid, string> names)
        => new(
            row.Id,
            row.Timestamp,
            row.Category,
            row.EventType,
            row.Severity.ToString(),
            row.ActorKind.ToString(),
            RenderActor(row, names),
            RenderTarget(row.TargetSubjectId, names),
            row.IpAddress,
            row.UserAgent,
            row.OAuthClientId,
            row.ApplicationId,
            row.SessionId,
            row.LoginProviderId,
            row.AuthenticationMethod,
            row.CorrelationId,
            row.OutcomeCode,
            row.ReasonCode,
            row.OperationCode,
            row.TargetRealmSlug,
            row.KeyId,
            row.Count,
            row.RelatedCount,
            row.RetentionDays,
            row.EffectiveAt,
            AuditEventRenderer.Render(row));

    private static string RenderActor(
        RealmSecurityAuditEvent row,
        IReadOnlyDictionary<Guid, string> names)
    {
        if (row.ActorSubjectId is { } subject)
            return names.TryGetValue(subject, out var name) ? name : "Deleted user";
        if (row.UnknownIdentifierFingerprint is { Length: > 0 } fingerprint)
            return $"Unknown identifier · {fingerprint[..Math.Min(10, fingerprint.Length)]}";
        if (!string.IsNullOrWhiteSpace(row.OAuthClientId))
            return row.OAuthClientId;

        return row.ActorKind switch
        {
            AuditActorKind.ControlPlane => "Control Plane",
            AuditActorKind.System => "System",
            AuditActorKind.ServiceAccount => "Service account",
            AuditActorKind.OAuthClient => "OAuth client",
            _ => row.ActorKind.ToString(),
        };
    }

    private static string? RenderTarget(Guid? subject, IReadOnlyDictionary<Guid, string> names)
        => subject is null
            ? null
            : names.TryGetValue(subject.Value, out var name) ? name : "Deleted user";
}

public sealed record PlatformAuditLogDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string Category,
    string EventType,
    string Severity,
    string OutcomeCode,
    string? ReasonCode,
    string? OperationCode,
    string? TargetRealmSlug,
    string? CorrelationId,
    int? Count,
    int? RelatedCount,
    string Message)
{
    internal static PlatformAuditLogDto From(PlatformAuditEvent row)
        => new(
            row.Id,
            row.Timestamp,
            row.Category,
            row.EventType,
            row.Severity.ToString(),
            row.OutcomeCode,
            row.ReasonCode,
            row.OperationCode,
            row.TargetRealmSlug,
            row.CorrelationId,
            row.Count,
            row.RelatedCount,
            AuditEventRenderer.Render(row));
}

internal static class AuditEventRenderer
{
    public static string Render(RealmSecurityAuditEvent row)
    {
        var details = new List<string>();
        Add(details, "reason", row.ReasonCode);
        Add(details, "operation", row.OperationCode);
        Add(details, "target-realm", row.TargetRealmSlug);
        Add(details, "client", row.OAuthClientId);
        Add(details, "key", row.KeyId);
        Add(details, "count", row.Count);
        Add(details, "related", row.RelatedCount);
        Add(details, "retention-days", row.RetentionDays);
        Add(details, "reminded", row.RemindedCount);
        Add(details, "self-erased", row.SelfErasedCount);
        Add(details, "auto-purged", row.AutoPurgedCount);
        Add(details, "invite-codes-pruned", row.InviteCodesPrunedCount);
        Add(details, "reused", row.ReusedCount);
        Add(details, "effective-at", row.EffectiveAt);
        return Compose(row.EventType, row.OutcomeCode, details);
    }

    public static string Render(PlatformAuditEvent row)
    {
        var details = new List<string>();
        Add(details, "reason", row.ReasonCode);
        Add(details, "operation", row.OperationCode);
        Add(details, "realm", row.TargetRealmSlug);
        Add(details, "domain", row.Domain);
        Add(details, "previous-domain", row.PreviousDomain);
        Add(details, "count", row.Count);
        Add(details, "related", row.RelatedCount);
        Add(details, "retention-days", row.RetentionDays);
        Add(details, "effective-at", row.EffectiveAt);
        return Compose(row.EventType, row.OutcomeCode, details);
    }

    private static string Compose(
        string eventType,
        string outcome,
        IReadOnlyCollection<string> details)
    {
        var occurrence = eventType switch
        {
            AuditEvents.LoginFailedUnknownUser => "Login for an unknown identifier",
            AuditEvents.MagicLinkInvalid => "Invalid or expired magic link",
            AuditEvents.ExternalLoginRejected => "External login",
            AuditEvents.SamlSignatureRejected => "SAML signature validation",
            AuditEvents.IdentityHijackBlocked => "External identity takeover attempt",
            AuditEvents.JitEmailConflict => "JIT email conflict",
            AuditEvents.PrivilegeEscalationBlocked => "Federated privilege escalation",
            AuditEvents.RateLimitTriggered => "Rate limit",
            AuditEvents.RefreshTokenReuseDetected => "Refresh-token reuse",
            AuditEvents.DcrRegistrationRejected => "Dynamic client registration",
            AuditEvents.BootstrapInviteRejected => "Bootstrap invite",
            AuditEvents.SecurityRetentionChanged => "Security-log retention",
            AuditEvents.SigningKeyRotated => "Signing key rotation",
            AuditEvents.SigningKeyPurged => "Signing-key cleanup",
            AuditEvents.SamlCertRotated => "SAML certificate rotation",
            AuditEvents.SamlMetadataRefreshed => "SAML metadata refresh",
            AuditEvents.RecoveryCliInvoked => "Recovery CLI operation",
            AuditEvents.RealmProvisioned => "Realm provisioning",
            AuditEvents.RealmAdopted => "Realm adoption",
            AuditEvents.ControlPlaneTransferred => "Control-Plane transfer",
            AuditEvents.ControlPlaneRealmOperation => "Control-Plane realm operation",
            AuditEvents.AccountLifecycleSwept => "Account lifecycle sweep",
            AuditEvents.BootstrapInviteIssued => "Bootstrap invite issuance",
            AuditEvents.DcrClientRegistered => "Dynamic client registration",
            AuditEvents.DcrClientFirstUsed => "Dynamic client first use",
            AuditEvents.DcrClientGarbageCollected => "Dynamic client cleanup",
            _ => eventType,
        };

        return details.Count == 0
            ? $"{occurrence}: {outcome}"
            : $"{occurrence}: {outcome} ({string.Join(", ", details)})";
    }

    private static void Add(List<string> details, string key, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            details.Add($"{key}={value}");
    }
}
