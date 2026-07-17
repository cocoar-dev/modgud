using Marten.Schema;

namespace Modgud.Infrastructure.Audit;

/// <summary>
/// A flat, typed, NON-event-sourced row in the streamless security/ops store
/// (logging/audit redesign Track A — the half that has no aggregate stream). One
/// document per occurrence; lives cross-realm in the <b>system DB</b>, attributed
/// to a realm via <see cref="Realm"/> and scoped at read by the caller's realm +
/// <see cref="PlatformOnly"/> (carrying PR #50's <c>ScopeToCallerRealm</c> forward).
///
/// <para>This is the successor to the personal-data-bearing-but-streamless portion
/// of the old <c>AuthLogDocument</c>: unknown-actor login attempts, probes,
/// rate-limit hits, and operational actions. Processed under <b>Art. 6(1)(f)</b>
/// (security / fraud detection); <b>short hard retention is the proportionality
/// control</b> (a Quartz prune), NOT per-subject erasure — there is no subject
/// stream to attach these to. See the maintainers' <c>logging-audit-redesign</c> design note
/// §A.5 + the Legitimate-Interest Assessment.</para>
/// </summary>
[DocumentAlias("security_audit_entry")]
public class SecurityAuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Realm slug the event was emitted in (from <c>TenantContext.Current</c>
    /// at emit; background / no-tenant work is attributed to <c>system</c>). All rows
    /// share the system DB; this column scopes the admin read.</summary>
    public string? Realm { get; init; }

    /// <summary><see cref="AuditCategories"/> code (derived from the event type).</summary>
    public string Category { get; init; } = "";

    /// <summary><see cref="AuditEvents"/> code (a streamless <c>security.* / ops.* /
    /// audit.*</c> code).</summary>
    public string EventType { get; init; } = "";

    /// <summary>"Info" | "Warning" | "Error".</summary>
    public string Level { get; init; } = "Info";

    /// <summary>True for control-plane-only events (cross-realm infra / platform ops).
    /// Derived from the event type at emit (<see cref="AuditEvents.IsPlatformOnly"/>)
    /// and stored so the read can filter on a column: a tenant realm-admin sees only
    /// <c>PlatformOnly == false</c> rows for their realm; the control-plane sees all.</summary>
    public bool PlatformOnly { get; init; }

    /// <summary>Who/what the event is about — an attempted username, masked email,
    /// acting admin, or IP. A display string, not a user-id GUID. May be personal
    /// data; retained only for the prune window. Surfaced as the grid's "user" column.</summary>
    public string? Actor { get; init; }

    /// <summary>Source IP where present. Personal data (CJEU <i>Breyer</i>) — retained
    /// only for the prune window.</summary>
    public string? Ip { get; init; }

    /// <summary>Coarse outcome ("rejected" | "succeeded" | "rotated" | …). Optional.</summary>
    public string? Status { get; init; }

    /// <summary>Disambiguating detail (rejection reason, recovery-CLI operation, …).</summary>
    public string? Reason { get; init; }

    /// <summary>Human-readable rendering for the admin grid (carry-forward of the
    /// legacy <c>Message</c> column).</summary>
    public string Message { get; init; } = "";
}
