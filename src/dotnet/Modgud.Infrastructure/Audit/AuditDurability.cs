namespace Modgud.Infrastructure.Audit;

/// <summary>
/// Delivery and persistence contract for streamless security/operations events.
/// The class is part of the event taxonomy so a call site cannot silently choose
/// a weaker guarantee than the event requires.
/// </summary>
public enum AuditDurabilityClass
{
    /// <summary>
    /// Privileged or irreversible state transition. It must be persisted, or
    /// enrolled in the same transactional outbox as the state change.
    /// </summary>
    Required,

    /// <summary>
    /// Individual takeover/tamper incident without a normal state transaction.
    /// The rejecting request waits for durable persistence.
    /// </summary>
    Incident,

    /// <summary>
    /// Potentially attacker-amplified signal. Raw occurrences may be dropped or
    /// sampled, while bounded batches are persisted as count aggregates.
    /// </summary>
    Abuse,

    /// <summary>
    /// Reconstructable operational information. Explicitly best-effort.
    /// </summary>
    Telemetry,
}

public static class AuditDurability
{
    public static AuditDurabilityClass Classify(string eventType) => eventType switch
    {
        AuditEvents.RefreshTokenReuseDetected or
        AuditEvents.AuditLogExported or
        AuditEvents.SecurityRetentionChanged or
        AuditEvents.SigningKeyRotated or
        AuditEvents.SamlCertRotated or
        AuditEvents.SamlSigningCertificatesChanged or
        AuditEvents.RecoveryCliInvoked or
        AuditEvents.RealmProvisioned or
        AuditEvents.RealmAdopted or
        AuditEvents.ControlPlaneTransferred or
        AuditEvents.InstallationChallengeIssued or
        AuditEvents.InstallationCompleted or
        AuditEvents.ControlPlaneRealmOperation or
        AuditEvents.BootstrapInviteIssued or
        AuditEvents.DcrClientRegistered
            => AuditDurabilityClass.Required,

        AuditEvents.ExternalLoginProtocolRejected or
        AuditEvents.SamlSignatureRejected or
        AuditEvents.IdentityHijackBlocked or
        AuditEvents.JitEmailConflict or
        AuditEvents.PrivilegeEscalationBlocked
            => AuditDurabilityClass.Incident,

        AuditEvents.LoginFailed or
        AuditEvents.LoginFailedUnknownUser or
        AuditEvents.MagicLinkInvalid or
        AuditEvents.ExternalLoginPolicyRejected or
        AuditEvents.RateLimitTriggered or
        AuditEvents.DcrRegistrationRejected or
        AuditEvents.BootstrapInviteRejected
            => AuditDurabilityClass.Abuse,

        AuditEvents.ExternalLoginConfigurationError or
        AuditEvents.SigningKeyPurged or
        AuditEvents.SamlMetadataRefreshCompleted or
        AuditEvents.AccountLifecycleSwept or
        AuditEvents.DcrClientFirstUsed or
        AuditEvents.DcrClientGarbageCollected or
        AuditEvents.StaffingSessionEnded
            => AuditDurabilityClass.Telemetry,

        _ => throw new ArgumentOutOfRangeException(
            nameof(eventType),
            eventType,
            "The streamless audit event has no durability classification."),
    };
}
