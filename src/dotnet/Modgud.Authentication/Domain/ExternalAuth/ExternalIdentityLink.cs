using System.Text.Json;

namespace Modgud.Authentication.Domain.ExternalAuth;

/// <summary>
/// A proven association between a Modgud user and an external identity asserted
/// by an IdP. Unique by <c>(Issuer, Subject)</c> globally — the same external
/// identity cannot map to two Modgud users.
/// <para>
/// One user can hold 0..n links (multi-IdP). Each link has its own event stream
/// so loading and auditing stay bounded as login volume grows.
/// </para>
/// </summary>
public class ExternalIdentityLink
{
    public Guid Id { get; set; }

    /// <summary>The Modgud user this link belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Which admin-registered login provider was used to establish this link.</summary>
    public Guid LoginProviderId { get; set; }

    /// <summary>OIDC <c>iss</c> claim — unique per IdP instance. Part of the natural key.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>OIDC <c>sub</c> claim — IdP-scoped unique identifier for the human. Part of the natural key.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Last-known email snapshot (denormalized from the user-update script output) — purely for admin display.</summary>
    public string? Email { get; set; }

    /// <summary>Last-known display name snapshot — purely for admin display.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset LastLoginAt { get; set; }

    /// <summary>
    /// True when this link's provider JIT-created the Modgud user (federation v1,
    /// decision A). Used to resolve the "JIT creator is profile-authoritative by
    /// default" fallback at profile-patch time, so a JIT-created user's profile is
    /// not silently frozen when no provider is explicitly authoritative.
    /// </summary>
    public bool IsCreator { get; set; }

    // ── Debugging snapshot of the last script run ─────────────────────
    // Exists purely for admin visibility (IdP-claims modal) and post-hoc
    // debugging. Not authoritative for anything — overwritten on every login.

    /// <summary>
    /// Raw IdP claim payload captured at the most recent login, if the IdP config
    /// has <c>StoreRawClaims = true</c>. PII-heavy; handle with care, respect
    /// <c>RawClaimsRetentionDays</c>.
    /// </summary>
    public JsonDocument? LastRawClaims { get; set; }

    /// <summary>
    /// The object the user-update script returned at the most recent login
    /// (<c>{ firstname, lastname, email, acronym }</c>-shape), serialized as
    /// JSON. Always stored — independent of <c>StoreRawClaims</c> — because
    /// this is the small, non-raw debugging artifact.
    /// </summary>
    public JsonDocument? LastScriptOutput { get; set; }

    /// <summary>Did the most recent script invocation complete without throwing?</summary>
    public bool LastScriptSucceeded { get; set; } = true;

    /// <summary>Error message from the most recent script invocation, if it failed.</summary>
    public string? LastScriptError { get; set; }

    /// <summary>When the most recent login + script run happened.</summary>
    public DateTimeOffset LastCapturedAt { get; set; }

    public bool IsUnlinked { get; set; }
}
