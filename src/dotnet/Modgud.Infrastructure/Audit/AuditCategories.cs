namespace Modgud.Infrastructure.Audit;

/// <summary>
/// Top-level audit categories. Drive the SPA filter chips and group the
/// <see cref="AuditEvents"/> vocabulary. Stable string codes (not display
/// text) — localise in the frontend, never compare against display strings.
///
/// <para>Lives in <c>Modgud.Infrastructure</c> (not <c>Authentication</c>, where
/// the Phase-0/2 stream-backed view lives) because the Phase-3 streamless
/// security/ops store has emit call sites in <i>lower</i> layers — notably
/// <c>RealmProvisioningService</c> in Infrastructure — that must reference these
/// codes without a magic string. Infrastructure is the lowest layer every call
/// site (Infrastructure / Authentication / Api) can reach.</para>
/// </summary>
public static class AuditCategories
{
    // ── Stream-backed (Track A — the GDPR-audit projection, AuthAuditView) ──
    public const string Authentication = "authentication";
    public const string Account = "account";
    public const string Federation = "federation";
    public const string AdminRealm = "admin-realm";
    public const string DcrOAuth = "dcr-oauth";

    // ── Streamless realm/platform security events ──
    /// <summary>Tenant-relevant security threats with no aggregate stream:
    /// unknown-actor login attempts, probes, rate-limit hits, policy rejections,
    /// and the audit-of-the-audit records.</summary>
    public const string SecurityOps = "security-ops";

    /// <summary>Operational actions (key/cert rotation, recovery-CLI, realm
    /// provisioning, sweeps). Storage scope is explicit at the call site through
    /// either a realm or platform record type.</summary>
    public const string Operations = "operations";
}
