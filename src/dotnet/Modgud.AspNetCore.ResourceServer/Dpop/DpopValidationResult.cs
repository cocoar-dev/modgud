// ─────────────────────────────────────────────────────────────────────────
// DUPLICATED, KEEP IN SYNC. Verbatim copy (namespace aside) of
// Modgud.Infrastructure/OpenIddict/Dpop/<same file>. The resource-server side
// needs the identical DPoP crypto, but this resource-server library is a published NuGet
// kept deliberately dependency-light, so the code is duplicated rather than
// shared. Any change to the server-side original MUST be mirrored here.
// ─────────────────────────────────────────────────────────────────────────

namespace Modgud.AspNetCore.ResourceServer.Dpop;

/// <summary>
/// Why a DPoP proof was rejected (RFC 9449 §4.3 / §5). <see cref="None"/> means
/// the proof passed every structural, cryptographic and temporal check — the
/// caller still owns replay detection (the <c>jti</c> uniqueness check against a
/// store), which is stateful and therefore not the validator's job.
/// </summary>
public enum DpopError
{
    None = 0,
    /// <summary>No proof was supplied.</summary>
    Missing,
    /// <summary>Not a well-formed three-segment JWS, or a segment failed to base64url/JSON decode.</summary>
    Malformed,
    /// <summary>Header <c>typ</c> is not the required <c>dpop+jwt</c>.</summary>
    InvalidType,
    /// <summary>Header <c>alg</c> is absent, <c>none</c>, symmetric, unknown, or mismatched with the key type.</summary>
    UnsupportedAlgorithm,
    /// <summary>The embedded <c>jwk</c> carries private key material — a public key is mandatory.</summary>
    ContainsPrivateKey,
    /// <summary>The JWS signature did not verify against the embedded public key.</summary>
    InvalidSignature,
    /// <summary>A required claim (<c>htm</c>/<c>htu</c>/<c>iat</c>/<c>jti</c>) is absent.</summary>
    MissingClaim,
    /// <summary>The proof's <c>htm</c> does not match the actual HTTP method.</summary>
    MethodMismatch,
    /// <summary>The proof's <c>htu</c> does not match the actual request URI.</summary>
    UriMismatch,
    /// <summary>The proof's <c>iat</c> is older than the accepted window.</summary>
    Expired,
    /// <summary>The proof's <c>iat</c> is too far in the future (beyond clock skew).</summary>
    FutureProof,
    /// <summary>A server nonce was required but the proof's <c>nonce</c> is absent or wrong.</summary>
    NonceMismatch,
    /// <summary>The proof's <c>ath</c> does not match the hash of the presented access token.</summary>
    AccessTokenHashMismatch,
}

/// <summary>
/// Outcome of validating a single DPoP proof. On success it carries the values
/// the caller needs downstream: <see cref="Jkt"/> (the confirmation thumbprint
/// to stamp as <c>cnf.jkt</c> at issuance, or to compare against the token's
/// <c>cnf.jkt</c> at validation), plus <see cref="Jti"/> and
/// <see cref="IssuedAt"/> for the caller's replay-cache bookkeeping.
/// </summary>
public sealed record DpopValidationResult
{
    /// <summary>True only when <see cref="Error"/> is <see cref="DpopError.None"/>.</summary>
    public bool IsValid => Error == DpopError.None;

    public required DpopError Error { get; init; }

    /// <summary>RFC 7638 thumbprint of the proof's public key (set on success only).</summary>
    public string? Jkt { get; init; }

    /// <summary>The proof's <c>jti</c> — the caller records this to reject replays.</summary>
    public string? Jti { get; init; }

    /// <summary>The proof's <c>iat</c> — lets the caller scope the replay entry's TTL.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    public static DpopValidationResult Fail(DpopError error) => new() { Error = error };

    public static DpopValidationResult Success(string jkt, string jti, DateTimeOffset issuedAt) =>
        new() { Error = DpopError.None, Jkt = jkt, Jti = jti, IssuedAt = issuedAt };
}
