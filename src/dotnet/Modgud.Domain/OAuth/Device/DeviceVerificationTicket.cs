namespace Modgud.Domain.OAuth.Device;

/// <summary>
/// Server-side ticket for the OAuth 2.0 Device Authorization Grant (RFC 8628)
/// end-user verification step. Created at <c>GET /connect/verify</c> once the
/// user is authenticated, consumed when the user approves/denies on the hosted
/// <c>/device</c> page.
///
/// <para>
/// Mirrors <see cref="Consent.ConsentTicket"/>: the OpenIddict verification
/// request is processed server-side, a subject-bound ticket is persisted, and
/// the SPA only ever sees the opaque ticket id — never the raw OpenIddict URL.
/// </para>
///
/// <list type="bullet">
///   <item><description><b>Subject binding</b> — the ticket is bound to the
///   authenticated user at creation; the read/decision endpoints reject any
///   other principal (no cross-user device approval).</description></item>
///   <item><description><b>User code</b> — captured from
///   <c>verification_uri_complete</c> when present; otherwise null until the
///   user types it on the <c>/device</c> page.</description></item>
///   <item><description><b>Single-use + short TTL</b> — <see cref="ConsumedAt"/>
///   plus <see cref="ExpiresAt"/>; a janitor trims expired records.</description></item>
/// </list>
/// </summary>
public class DeviceVerificationTicket
{
    /// <summary>Random opaque identifier surfaced as the <c>ticket</c> URL param
    /// to the SPA. Use <see cref="Guid.CreateVersion7"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>User id (matches the session's <c>sub</c>) that opened the
    /// verification page. Read/decision endpoints MUST reject other principals.</summary>
    public Guid Subject { get; set; }

    /// <summary>The RFC 8628 <c>user_code</c> the device displayed. Captured from
    /// <c>verification_uri_complete</c> at creation when present; otherwise set
    /// when the user submits it on the <c>/device</c> page. Null = not entered yet.</summary>
    public string? UserCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set on the first approve/deny decision. Subsequent decisions on
    /// the same ticket are rejected — single-use enforced.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
