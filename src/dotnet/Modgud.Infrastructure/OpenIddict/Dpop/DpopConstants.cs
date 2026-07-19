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
}
