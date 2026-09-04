using System.Security.Cryptography;
using System.Text;
using Modgud.Authentication.Events;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// ADR 0009 — "this relying party holds tokens of this session". One row per session
/// and client, upserted whenever an access token is minted for the pair. Answers
/// "which RPs does this session touch?" (back-channel logout fan-out) and "which
/// sessions does this App see?" (change feed). Plain document in the realm database:
/// not event-sourced, not soft-deleted; hard-deleted with the session, by the
/// retention sweep and by GDPR erasure.
/// </summary>
public sealed class SessionGrant
{
    public Guid Id { get; set; }

    /// <summary>The <c>sid</c>: <c>UserSession.Id</c> for browser sessions,
    /// <c>ClientSession.Id</c> for native ones.</summary>
    public Guid SessionId { get; set; }

    public Guid UserId { get; set; }

    public string ClientId { get; set; } = string.Empty;

    /// <summary>OpenIddict application primary key (empty for synthesized CIMD clients).</summary>
    public string ApplicationId { get; set; } = string.Empty;

    public AccessSessionKind Kind { get; set; }

    /// <summary>The <c>iss</c> the session's tokens carried. A logout token must repeat
    /// it exactly, and a background sender has no request to derive it from.</summary>
    public string Issuer { get; set; } = string.Empty;

    public DateTimeOffset FirstIssuedAt { get; set; }

    public DateTimeOffset LastIssuedAt { get; set; }

    /// <summary>Deterministic id so the upsert on every token mint needs no lookup race.</summary>
    public static Guid IdFor(Guid sessionId, string clientId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"session-grant|{sessionId:N}|{clientId}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}
