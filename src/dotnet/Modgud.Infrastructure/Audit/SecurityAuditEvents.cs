using Marten.Schema;

namespace Modgud.Infrastructure.Audit;

/// <summary>
/// One structured security occurrence owned by exactly one realm. The document is
/// stored in that realm's physical database; it therefore has no Realm column.
/// Personal data is allowed only in the explicit forensic fields below and is
/// hard-deleted by the realm's configurable retention job.
/// </summary>
[DocumentAlias("realm_security_audit_event")]
public sealed class RealmSecurityAuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string EventType { get; init; } = "";
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
    public AuditActorKind ActorKind { get; init; } = AuditActorKind.System;
    public Guid? ActorSubjectId { get; init; }
    public Guid? TargetSubjectId { get; init; }
    public string? UnknownIdentifierFingerprint { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? OAuthClientId { get; init; }
    public string? AuthorizationId { get; init; }
    public Guid? ApplicationId { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? LoginProviderId { get; init; }
    public string? AuthenticationMethod { get; init; }
    public string? CorrelationId { get; init; }
    public string OutcomeCode { get; init; } = AuditOutcomes.Observed;
    public string? ReasonCode { get; init; }
    public string? OperationCode { get; init; }
    public string? TargetRealmSlug { get; init; }
    public string? KeyId { get; init; }
    public int? Count { get; init; }
    public int? RelatedCount { get; init; }
    public int? RemindedCount { get; init; }
    public int? SelfErasedCount { get; init; }
    public int? AutoPurgedCount { get; init; }
    public int? InviteCodesPrunedCount { get; init; }
    public int? ReusedCount { get; init; }
    public int? RetentionDays { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
    public DateTimeOffset? FirstObservedAt { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
}

/// <summary>
/// Deployment-wide operations event. This type deliberately has no subject,
/// identifier, IP, user-agent, client, application or session field. It lives
/// only in the non-tenanted Global Store.
/// </summary>
[DocumentAlias("platform_audit_event")]
public sealed class PlatformAuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string EventType { get; init; } = "";
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
    public string OutcomeCode { get; init; } = AuditOutcomes.Observed;
    public string? ReasonCode { get; init; }
    public string? OperationCode { get; init; }
    public string? TargetRealmSlug { get; init; }
    public string? Domain { get; init; }
    public string? PreviousDomain { get; init; }
    public string? CorrelationId { get; init; }
    public int? Count { get; init; }
    public int? RelatedCount { get; init; }
    public int? RetentionDays { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
}

/// <summary>
/// Per-realm secret used to turn an unresolved login/reset identifier into a
/// stable HMAC fingerprint. The raw identifier is never persisted. A separate
/// random key per physical realm prevents cross-realm correlation.
/// </summary>
[DocumentAlias("realm_audit_fingerprint_key")]
public sealed class RealmAuditFingerprintKey
{
    public const string SingletonId = "realm-security-audit-hmac-v1";

    public string Id { get; init; } = SingletonId;
    public required byte[] Key { get; init; }
}

public enum AuditSeverity
{
    Info,
    Warning,
    Error,
}

public enum AuditActorKind
{
    User,
    AnonymousIdentifier,
    OAuthClient,
    ServiceAccount,
    ControlPlane,
    System,
}

public static class AuditOutcomes
{
    public const string Observed = "observed";
    public const string Succeeded = "succeeded";
    public const string Rejected = "rejected";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Initiated = "initiated";
    public const string Completed = "completed";
    public const string Pruned = "pruned";
}
