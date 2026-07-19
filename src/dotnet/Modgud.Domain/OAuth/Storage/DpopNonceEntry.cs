namespace Modgud.Domain.OAuth.Storage;

/// <summary>
/// A server-issued DPoP nonce (RFC 9449 §8-9), recorded so the authorization
/// server can confirm that a nonce presented in a later proof is one it actually
/// minted and is still fresh. Stored per-realm (the tenant-scoped Marten session
/// routes it to the calling realm's database) so a nonce issued for one realm can
/// never satisfy a proof presented to another. <see cref="Id"/> is the opaque
/// nonce value; <see cref="ExpiresAt"/> bounds its acceptance window and lets the
/// store prune spent entries.
/// </summary>
public sealed class DpopNonceEntry
{
    /// <summary>The opaque nonce value — the natural key.</summary>
    public string Id { get; set; } = default!;

    /// <summary>When the nonce stops being accepted (and may be pruned).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
