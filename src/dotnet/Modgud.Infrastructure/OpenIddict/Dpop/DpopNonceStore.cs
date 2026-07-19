using System.Security.Cryptography;
using Marten;
using Modgud.Domain.OAuth.Storage;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Issues and validates server DPoP nonces (RFC 9449 §8-9). Backed by the
/// tenant-scoped Marten session, so a nonce minted on one app instance is
/// honoured on every instance pointed at the same realm database, and is scoped
/// to that realm.
/// </summary>
public interface IDpopNonceStore
{
    /// <summary>Mints a fresh nonce, records it with its acceptance window, and
    /// returns the opaque value to hand back in the <c>DPoP-Nonce</c> header.</summary>
    Task<string> IssueAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>True when <paramref name="nonce"/> is one this server issued and
    /// has not yet expired.</summary>
    Task<bool> IsValidAsync(string nonce, DateTimeOffset now, CancellationToken ct);
}

internal sealed class MartenDpopNonceStore : IDpopNonceStore
{
    /// <summary>How long a freshly issued nonce is accepted. The client reuses it
    /// across requests until it lapses; the next request then triggers a fresh
    /// <c>use_dpop_nonce</c> handshake. Short enough to bound the ledger, long
    /// enough to avoid a handshake on every call.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDocumentSession _session;

    public MartenDpopNonceStore(IDocumentSession session) => _session = session;

    public async Task<string> IssueAsync(DateTimeOffset now, CancellationToken ct)
    {
        // 32 bytes of CSPRNG entropy, URL-safe so it rides an HTTP header cleanly.
        var nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        // Opportunistic prune of lapsed nonces — keeps the table bounded without a
        // separate job (the ExpiresAt index makes this a small range delete).
        _session.DeleteWhere<DpopNonceEntry>(x => x.ExpiresAt < now);
        _session.Store(new DpopNonceEntry { Id = nonce, ExpiresAt = now + Lifetime });
        await _session.SaveChangesAsync(ct);
        return nonce;
    }

    public async Task<bool> IsValidAsync(string nonce, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        var entry = await _session.LoadAsync<DpopNonceEntry>(nonce, ct);
        return entry is not null && entry.ExpiresAt > now;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
