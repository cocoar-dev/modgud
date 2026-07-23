namespace Modgud.Infrastructure.Audit;

/// <summary>
/// Canonical, stable event-type codes for the tenant audit trail. The successor
/// to the <c>"Auth:"</c>-message-prefix vocabulary: both the projection that writes
/// <c>AuthAuditView</c> rows and the streamless security/ops store reference these
/// constants, so a rename can't silently desync a writer from a reader.
///
/// <para><b>Two families, one vocabulary.</b> The <c>auth.* / account.* /
/// federation.* / admin.*</c> codes name occurrences on the <i>user- and config-
/// aggregate streams</i> (projected into the per-realm GDPR-audit view). The
/// <c>security.* / ops.* / audit.*</c> codes name <i>streamless</i> occurrences
/// routed either to the owning realm's <c>RealmSecurityAuditEvent</c> store or,
/// for genuinely deployment-wide work, to the PII-free Global Store
/// <c>PlatformAuditEvent</c>.</para>
///
/// <para><b>PII discipline:</b> these name <i>occurrences</i>, not payloads. The
/// stream-backed rows store only metadata (who/when/what-kind/realm) and inherit
/// per-subject GDPR masking from the source events. The streamless rows may carry
/// an attempted identifier / IP (personal data under CJEU <i>Breyer</i>) — the
/// short retention window is the proportionality control, not per-subject erase.</para>
/// </summary>
public static class AuditEvents
{
    // ─────────────────────────────────────────────────────────────────────
    // Stream-backed (Track A — projected into AuthAuditView, GDPR-erasable)
    // ─────────────────────────────────────────────────────────────────────

    // ── Authentication (user-stream) ─────────────────────────────────
    /// <summary>A successful login. Marker only — IP/device live in the
    /// Sessions feature, not on the event. Fields: UserId.</summary>
    public const string LoginSucceeded = "auth.login_succeeded";

    /// <summary>A failed login against a KNOWN user (the streamless store
    /// holds unknown-actor attempts). Fields: UserId, Ip.</summary>
    public const string LoginFailed = "auth.login_failed";

    /// <summary>An aggregated known-user failure streak (Decision (b)) — one row
    /// per resolved streak, carrying the count, not one per attempt. Fields:
    /// UserId, Count.</summary>
    public const string LoginFailuresObserved = "auth.login_failures_observed";

    /// <summary>Account crossed the lockout threshold. Fields: UserId.</summary>
    public const string AccountLockedOut = "auth.locked_out";

    /// <summary>Lockout cleared/expired. Fields: UserId.</summary>
    public const string AccountUnlocked = "auth.unlocked";

    // ── Account lifecycle (user-stream) ──────────────────────────────
    public const string AccountCreated = "account.created";
    public const string AccountDeleted = "account.deleted";
    public const string AccountProfileUpdated = "account.profile_updated";
    public const string AccountUserNameChanged = "account.username_changed";
    public const string AccountPasswordChanged = "account.password_changed";
    public const string AccountActivated = "account.activated";
    public const string AccountDeactivated = "account.deactivated";

    // ── Federation (user-stream mirror events) ───────────────────────
    public const string IdentityLinked = "federation.identity_linked";
    public const string IdentityUnlinked = "federation.identity_unlinked";

    // ── Admin / realm config (config-aggregate streams) ──────────────
    public const string LoginProviderAdded = "admin.login_provider_added";
    public const string LoginProviderUpdated = "admin.login_provider_updated";
    public const string LoginProviderEnabled = "admin.login_provider_enabled";
    public const string LoginProviderDisabled = "admin.login_provider_disabled";
    public const string LoginProviderSecretRotated = "admin.login_provider_secret_rotated";
    public const string LoginProviderDeleted = "admin.login_provider_deleted";

    // ─────────────────────────────────────────────────────────────────────
    // Streamless realm/platform security events
    // ─────────────────────────────────────────────────────────────────────

    // ── Security: streamless threats (tenant-visible) ────────────────
    /// <summary>Login attempt against a username/email matching no active user
    /// (password or magic-link). Actor = attempted identifier; carries Ip.</summary>
    public const string LoginFailedUnknownUser = "security.login_failed_unknown_user";

    /// <summary>Magic-link login with an invalid/expired token (anonymous probe).
    /// Carries Ip.</summary>
    public const string MagicLinkInvalid = "security.magic_link_invalid";

    /// <summary>An external/federation login was rejected before any user link —
    /// domain allowlist, JIT disabled, inactive user, malformed token, or a
    /// misconfigured provider. <c>Reason</c> disambiguates. Covers the SAML
    /// protocol gates (no metadata, no SSO endpoint, context-build / response-read
    /// failure, non-success status) as well as the OIDC/processor rejections.</summary>
    public const string ExternalLoginRejected = "security.external_login_rejected";

    /// <summary>A SAML response failed the admin-required signature check
    /// (response/assertion unsigned). A distinct <b>tamper / signature-wrapping</b>
    /// attack signal — not a config/transport problem — so it gets its own code.
    /// <c>Reason</c> carries the failing tag (response-unsigned / assertion-unsigned / …).</summary>
    public const string SamlSignatureRejected = "security.saml_signature_rejected";

    /// <summary>A link attempt was rejected because the external subject is already
    /// linked to a DIFFERENT user (attempted account takeover).</summary>
    public const string IdentityHijackBlocked = "security.identity_hijack_blocked";

    /// <summary>JIT / user-update-script create blocked — the email is already taken
    /// by another user (prevents takeover via auto-provisioning).</summary>
    public const string JitEmailConflict = "security.jit_email_conflict";

    /// <summary>Externally-derived group(s) conferring <c>realm:admin</c> were
    /// dropped at login (federation privilege-escalation guard).</summary>
    public const string PrivilegeEscalationBlocked = "security.privilege_escalation_blocked";

    /// <summary>A rate limit was triggered (DCR or login surface). Actor = Ip.</summary>
    public const string RateLimitTriggered = "security.rate_limit_triggered";

    /// <summary>An already-redeemed refresh token was re-presented at
    /// <c>/connect/token</c> (RFC 6749 §10.4 reuse signal) — the canonical
    /// indicator that the token was captured by an attacker. The whole
    /// authorization's token chain is revoked as teardown; this event is the
    /// forensic record of that revoke. Actor = UserId (subject on the reused
    /// token). <c>Reason</c> carries the client id, authorization id, and the
    /// count of sibling tokens revoked.</summary>
    public const string RefreshTokenReuseDetected = "security.refresh_token_reuse_detected";

    /// <summary>A DCR client registration was rejected (policy / validation).</summary>
    public const string DcrRegistrationRejected = "security.dcr_registration_rejected";

    /// <summary>A bootstrap-admin invite consume was rejected (wrong/expired code).
    /// Carries Ip. NB: the invite code itself is never stored.</summary>
    public const string BootstrapInviteRejected = "security.bootstrap_invite_rejected";

    // ── Audit-of-the-audit (tenant-visible) ──────────────────────────
    /// <summary>The audit/security log was exported by an operator.</summary>
    public const string AuditLogExported = "audit.log_exported";

    /// <summary>A realm admin changed the hard retention of realm security events.</summary>
    public const string SecurityRetentionChanged = "audit.security_retention_changed";

    // ── Operations: realm/platform actions ───────────────────────────
    /// <summary>A realm signing key was rotated by an admin (tenant-visible).</summary>
    public const string SigningKeyRotated = "ops.signing_key_rotated";

    /// <summary>The signing-key janitor purged expired retired keys (platform-only).</summary>
    public const string SigningKeyPurged = "ops.signing_key_purged";

    /// <summary>A realm's SAML SP certificate was rotated or first generated
    /// (tenant-visible — a realm-relevant trust change).</summary>
    public const string SamlCertRotated = "ops.saml_cert_rotated";

    /// <summary>Background SAML metadata refresh tick / IdP signing-cert change
    /// (platform-only).</summary>
    public const string SamlMetadataRefreshed = "ops.saml_metadata_refreshed";

    /// <summary>A recovery-CLI operation was invoked (filesystem-trust, control-plane
    /// only). <c>Reason</c> carries the specific operation + parameters.</summary>
    public const string RecoveryCliInvoked = "ops.recovery_cli_invoked";

    /// <summary>A realm database was provisioned (platform-only). Closes the gap
    /// where <c>RealmProvisioningService</c> logged without the <c>"Auth:"</c>
    /// prefix and never reached the legacy log at all.</summary>
    public const string RealmProvisioned = "ops.realm_provisioned";

    /// <summary>An existing database was adopted as a realm (platform-only).</summary>
    public const string RealmAdopted = "ops.realm_adopted";

    /// <summary>The control-plane role was transferred to another realm
    /// (platform-only).</summary>
    public const string ControlPlaneTransferred = "ops.control_plane_transferred";

    /// <summary>A Control-Plane actor changed one explicitly selected realm.</summary>
    public const string ControlPlaneRealmOperation = "ops.control_plane_realm_operation";

    /// <summary>A per-realm account-lifecycle sweep ran (reminders / self-erase /
    /// auto-purge counts). Platform-only operational summary.</summary>
    public const string AccountLifecycleSwept = "ops.account_lifecycle_swept";

    /// <summary>A bootstrap-admin invite was issued (tenant-visible realm-init).
    /// Any email is masked at the call site.</summary>
    public const string BootstrapInviteIssued = "ops.bootstrap_invite_issued";

    /// <summary>A DCR client was registered (tenant-visible).</summary>
    public const string DcrClientRegistered = "ops.dcr_client_registered";

    /// <summary>A registered DCR client was used for the first time — a clean signal
    /// the registration was real, not bot noise (tenant-visible).</summary>
    public const string DcrClientFirstUsed = "ops.dcr_client_first_used";

    /// <summary>A DCR client was garbage-collected for inactivity (tenant-visible).</summary>
    public const string DcrClientGarbageCollected = "ops.dcr_client_garbage_collected";

    // ─────────────────────────────────────────────────────────────────────
    // Taxonomy helper. Store ownership is selected through separate realm and
    // platform record types, never inferred from this event code.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>The <see cref="AuditCategories"/> code an event type belongs to,
    /// derived from its prefix. Used by the streamless sink to stamp the row.</summary>
    public static string CategoryOf(string eventType) => eventType switch
    {
        _ when eventType.StartsWith("ops.", StringComparison.Ordinal) => AuditCategories.Operations,
        _ when eventType.StartsWith("security.", StringComparison.Ordinal)
            || eventType.StartsWith("audit.", StringComparison.Ordinal) => AuditCategories.SecurityOps,
        _ when eventType.StartsWith("account.", StringComparison.Ordinal) => AuditCategories.Account,
        _ when eventType.StartsWith("federation.", StringComparison.Ordinal) => AuditCategories.Federation,
        _ when eventType.StartsWith("admin.", StringComparison.Ordinal) => AuditCategories.AdminRealm,
        _ => AuditCategories.Authentication, // auth.*
    };

}
