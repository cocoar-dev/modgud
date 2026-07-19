namespace Modgud.Domain.OAuth.Storage;

/// <summary>
/// A record that a given DPoP proof <c>jti</c> has been seen, used to reject
/// replays (RFC 9449 §11.1). Stored per-realm (the tenant-scoped Marten session
/// routes it to the calling realm's database) so a proof accepted in one realm
/// can't be replayed in another. <see cref="Id"/> is the proof's <c>jti</c>;
/// <see cref="ExpiresAt"/> lets the store prune entries once the proof could no
/// longer be accepted anyway (past the proof max-age + skew window).
/// </summary>
public sealed class DpopReplayEntry
{
    /// <summary>The DPoP proof's <c>jti</c> — the natural key.</summary>
    public string Id { get; set; } = default!;

    /// <summary>When this entry may be pruned (proof issue time + max-age + skew).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
