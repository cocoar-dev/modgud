namespace Modgud.Infrastructure.Audit;

/// <summary>
/// One streamless security/ops occurrence to record. The caller supplies the
/// <see cref="AuditEvents"/> code plus whatever context it has; the sink derives
/// the <c>Category</c> + control-plane visibility from the code (the taxonomy is
/// the source of truth) and stamps the realm + timestamp at emit.
///
/// <para><b>PII is the caller's responsibility to minimise.</b> Pass an attempted
/// username / masked email / IP as <see cref="Actor"/> only where it is the
/// security signal; never put secrets, tokens, or invite codes in any field.</para>
/// </summary>
public sealed record SecurityAuditRecord
{
    /// <summary>An <see cref="AuditEvents"/> streamless code (<c>security.* /
    /// ops.* / audit.*</c>).</summary>
    public required string EventType { get; init; }

    /// <summary>Explicit realm slug, overriding the ambient
    /// <c>TenantContext.Current</c>. Set this from <b>realm-iterating background
    /// jobs</b> (the signing-key janitor, DCR GC, lifecycle sweep, realm
    /// provisioning) which run in the <c>system</c> session but emit per-realm
    /// rows — exactly the case the legacy <c>RealmLogEnricher</c>'s explicit
    /// <c>{Realm}</c> binding handled. Leave null on the request path (the ambient
    /// realm is correct there).</summary>
    public string? Realm { get; init; }

    /// <summary>"Info" | "Warning" | "Error" — the legacy level mapping.</summary>
    public string Level { get; init; } = "Info";

    /// <summary>Who/what the event is about: an attempted username, a masked email,
    /// an acting admin's username, or an IP for a purely anonymous actor. A display
    /// string (NOT a user-id GUID) so the cross-realm read needs no per-tenant join.
    /// Null when there is no meaningful actor.</summary>
    public string? Actor { get; init; }

    /// <summary>Source IP where the event carries one. Personal data under CJEU
    /// <i>Breyer</i> — retained only for the short prune window.</summary>
    public string? Ip { get; init; }

    /// <summary>Coarse outcome, e.g. "rejected" | "succeeded" | "rotated". Optional.</summary>
    public string? Status { get; init; }

    /// <summary>Disambiguating detail (e.g. the rejection reason, the recovery-CLI
    /// operation). Already PII-minimised by the caller.</summary>
    public string? Reason { get; init; }

    /// <summary>Human-readable rendering for the admin grid (carried forward from the
    /// legacy free-text <c>Message</c> column so the existing view keeps working).</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// Best-effort sink for the streamless security/ops audit store (Track A, Phase 3).
/// Replaces the <c>"Auth:"</c>-message-prefix Serilog sink: call sites emit a typed
/// <see cref="SecurityAuditRecord"/> instead of stringly-typed log lines.
///
/// <para><b>Contract:</b> <see cref="Record"/> is non-blocking and NEVER throws — a
/// failed enqueue drops the record rather than break the auth flow. The realm is
/// captured from <c>TenantContext.Current</c> at call time (the background writer
/// runs tenant-less). Durability is best-effort by design: this is a short-retention
/// legitimate-interest store, not the per-subject GDPR audit (which is the
/// event-sourced <c>AuthAuditView</c>).</para>
/// </summary>
public interface ISecurityAuditLog
{
    /// <summary>Enqueue a streamless security/ops record. Non-blocking, never throws.</summary>
    void Record(SecurityAuditRecord record);
}
