using Marten.Schema;
using Modgud.Infrastructure.Audit;

namespace Modgud.Authentication.Audit;

/// <summary>
/// Flat, per-event tenant audit row — one document per audited event, projected
/// from the user- and config-aggregate streams by <see cref="AuthAuditViewProjection"/>.
/// This is the derived read model for the GDPR-audit (it replaces the personal-data
/// portion of the old flat <c>AuthLogDocument</c>); durability + GDPR masking are
/// inherited from the source events, so the view is freely rebuildable.
///
/// <para><b>Metadata only — no payloads.</b> The row records who/when/what-kind/realm,
/// never the changed values. Personal data stays on the source streams (masked on
/// erase); a permanent-erase deletes a user's rows here (Phase-2 scrub —
/// <c>DeleteWhere&lt;AuthAuditView&gt;(x =&gt; x.UserId == userId)</c>).</para>
///
/// <para>Lives per-realm in each tenant DB (physical isolation — a realm cannot read
/// another realm's audit). <see cref="Realm"/> is carried for the control-plane
/// cross-realm fan-out and parity with the legacy log; it is the event's tenant id.</para>
/// </summary>
[DocumentAlias("auth_audit_view")]
public record AuthAuditView
{
    /// <summary>The Marten event id — one audit row per event occurrence.</summary>
    public Guid Id { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Realm slug the event was emitted in (the event's tenant id).</summary>
    public string? Realm { get; init; }

    /// <summary><see cref="AuditCategories"/> code.</summary>
    public string Category { get; init; } = "";

    /// <summary><see cref="AuditEvents"/> code.</summary>
    public string EventType { get; init; } = "";

    /// <summary>The data subject, for user-stream events (= the user stream id).
    /// Null for config-aggregate events.</summary>
    public Guid? UserId { get; init; }

    /// <summary>The config aggregate (e.g. login-provider) id, for config-stream
    /// events. Null for user-stream events.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>Denormalised display name. Null in the Phase-0 scaffold — resolved
    /// from <c>UserView</c> at read time, or denormalised in a later phase.</summary>
    public string? UserName { get; init; }

    /// <summary>Source IP where the event carries one (e.g. a login). PII — inherits
    /// the source event's GDPR masking, and the row is deleted on permanent erase.</summary>
    public string? Ip { get; init; }

    /// <summary>Login method code for login events ("password" | "magic_link" |
    /// "external" | …), null otherwise. Non-PII — a method switch is a security signal.</summary>
    public string? Method { get; init; }

    /// <summary>Aggregate count for summary events (e.g. the failed-attempt count on
    /// an <c>auth.login_failures_observed</c> row). Null for single-occurrence rows.</summary>
    public int? Count { get; init; }

    /// <summary>"Info" | "Warning" | "Error" — preserves the legacy level mapping.</summary>
    public string Level { get; init; } = "Info";
}
