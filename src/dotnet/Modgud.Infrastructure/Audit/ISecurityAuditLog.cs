namespace Modgud.Infrastructure.Audit;

/// <summary>
/// Structured input for a realm-owned security event. <see cref="RealmSlug"/> is
/// routing metadata only and is never persisted in the realm document.
/// <see cref="UnknownIdentifier"/> is accepted transiently so the writer can HMAC
/// it with the owning realm's key; its raw value never reaches storage.
/// <see cref="CaptureRequestContext"/> is disabled for non-identifying
/// cross-realm counterpart events so actor PII stays in the actor's realm.
/// </summary>
public sealed record SecurityAuditRecord
{
    public required string EventType { get; init; }
    public string? RealmSlug { get; init; }
    public bool CaptureRequestContext { get; init; } = true;
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
    public AuditActorKind? ActorKind { get; init; }
    public Guid? ActorSubjectId { get; init; }
    public Guid? TargetSubjectId { get; init; }
    public string? UnknownIdentifier { get; init; }
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
}

/// <summary>
/// Structured deployment-wide event. The absence of subject, identifier, IP,
/// user-agent, client and session fields is an intentional compile-time privacy
/// boundary.
/// </summary>
public sealed record PlatformAuditRecord
{
    public required string EventType { get; init; }
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
/// Best-effort, non-blocking security-event sink. Realm and platform records use
/// separate methods and payload types so storage ownership and the PII boundary
/// cannot be selected through a boolean flag.
/// </summary>
public interface ISecurityAuditLog
{
    void Record(SecurityAuditRecord record);
    void RecordPlatform(PlatformAuditRecord record);
}
