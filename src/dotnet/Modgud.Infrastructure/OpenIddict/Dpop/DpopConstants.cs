namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Wire-format constants for DPoP (RFC 9449). These values cross the wire or key
/// persisted state, so they are pinned by <c>DpopConstantsTests</c> — a drift
/// would silently break the binding (a client's <c>cnf.jkt</c> no longer matching,
/// or a <c>DPoP</c> token being announced as <c>Bearer</c>).
/// </summary>
public static class DpopConstants
{
    /// <summary>The HTTP header carrying the proof JWT (RFC 9449 §4).</summary>
    public const string HeaderName = "DPoP";

    /// <summary>The <c>token_type</c> value for a DPoP-bound access token (§5).</summary>
    public const string TokenType = "DPoP";

    /// <summary>The confirmation claim name (RFC 7800 / §6).</summary>
    public const string ConfirmationClaim = "cnf";

    /// <summary>The JWK-thumbprint member inside <c>cnf</c> (§6).</summary>
    public const string JwkThumbprintMember = "jkt";

    /// <summary>The OAuth error returned for a missing/invalid proof (§5 / §7.1).</summary>
    public const string InvalidProofError = "invalid_dpop_proof";

    /// <summary>AS-metadata field listing the JWS algs accepted in a proof (§5.1).</summary>
    public const string SigningAlgValuesMetadataKey = "dpop_signing_alg_values_supported";

    /// <summary>
    /// <c>HttpContext.Items</c> key used to hand the validated proof's thumbprint
    /// from the proof-validation handler to the claim-stamping and token-type
    /// handlers. HttpContext is shared across every OpenIddict event for the one
    /// token request (unlike the per-event server transaction).
    /// </summary>
    public const string HttpContextJktKey = "modgud:dpop:jkt";

    /// <summary>
    /// Microsoft JWT value type marking a claim whose string value is raw JSON to be
    /// embedded as a nested object (so <c>cnf</c> serialises as <c>{"jkt":"…"}</c>,
    /// not as an escaped string).
    /// </summary>
    public const string JsonClaimValueType = "JSON";

    /// <summary>
    /// Internal carrier claim recording the DPoP key thumbprint a refresh token is
    /// bound to (RFC 9449 §5). Set once at the DPoP-proofed issuance that mints the
    /// refresh token, then re-copied onto each rotated refresh token so the chain
    /// stays bound. Persisted in the server-side reference token but yields NO
    /// destination — it never reaches an access/id token on the wire (a resource
    /// server reads the binding from <c>cnf.jkt</c>, not this). At the refresh grant
    /// the presented proof's thumbprint must equal it, or the grant is rejected.
    /// </summary>
    public const string RefreshBindingClaimType = "modgud:dpop:rt_jkt";
}
