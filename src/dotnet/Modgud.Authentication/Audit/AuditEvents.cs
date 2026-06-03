namespace Modgud.Authentication.Audit;

/// <summary>
/// Top-level audit categories. Drive the SPA filter chips and group the
/// <see cref="AuditEvents"/> vocabulary. Stable string codes (not display
/// text) — localise in the frontend, never compare against display strings.
/// </summary>
public static class AuditCategories
{
    public const string Authentication = "authentication";
    public const string Account = "account";
    public const string Federation = "federation";
    public const string AdminRealm = "admin-realm";
    public const string DcrOAuth = "dcr-oauth";
    public const string SecurityOps = "security-ops";
}

/// <summary>
/// Canonical, stable event-type codes for the tenant audit trail. The
/// successor to the <c>"Auth:"</c>-message-prefix vocabulary (and a sibling of
/// <see cref="Modgud.Application.Dcr.DcrAuditEvents"/>): both the projection
/// that writes <see cref="AuthAuditView"/> rows and the SPA grid filter
/// reference these constants, so a rename can't silently desync the two.
///
/// <para><b>PII discipline:</b> these name <i>occurrences</i>, not payloads.
/// The audit view stores only metadata (who/when/what-kind/realm) — never the
/// changed values. Personal data lives on the source event streams, where it is
/// already GDPR-masked at the <c>ApplyEventDataMasking</c> layer; the view rows
/// for an erased user are deleted by the Phase-2 erase-scrub. See
/// <c>dev-docs/future-features/logging-audit-redesign.md</c> §A.</para>
/// </summary>
public static class AuditEvents
{
    // ── Authentication (user-stream) ─────────────────────────────────
    /// <summary>A successful login. Marker only — IP/device live in the
    /// Sessions feature, not on the event. Fields: UserId.</summary>
    public const string LoginSucceeded = "auth.login_succeeded";

    /// <summary>A failed login against a KNOWN user (the streamless store
    /// holds unknown-actor attempts). Fields: UserId, Ip.</summary>
    public const string LoginFailed = "auth.login_failed";

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
}
