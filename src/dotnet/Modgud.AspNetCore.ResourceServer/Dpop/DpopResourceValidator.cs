using Microsoft.AspNetCore.Http;

namespace Modgud.AspNetCore.ResourceServer.Dpop;

/// <summary>Outcome of validating a DPoP proof presented at a resource server.</summary>
internal enum DpopResourceResult
{
    /// <summary>Proof is present, valid, bound to the access token, and its key matches <c>cnf.jkt</c>.</summary>
    Valid,
    /// <summary>No <c>DPoP</c> header on the request.</summary>
    NoProof,
    /// <summary>The proof is malformed, unsigned-correctly, stale, or bound to a different request/token.</summary>
    InvalidProof,
    /// <summary>The proof verified, but its key thumbprint ≠ the token's <c>cnf.jkt</c>.</summary>
    ThumbprintMismatch,
}

/// <summary>
/// Resource-server half of DPoP (RFC 9449 §7.2): confirms that the client
/// presenting a sender-constrained access token actually holds the bound key.
/// It validates the request's <c>DPoP</c> proof against the HTTP method + URL and
/// the access token's hash (<c>ath</c>), then checks the proof key's RFC 7638
/// thumbprint equals the token's <c>cnf.jkt</c>.
///
/// <para>Pure and stateless (clock injected). <c>jti</c> replay tracking at the
/// RS is intentionally out of scope for now — the AS already rejects replayed
/// proofs at issuance, and the <c>ath</c> + <c>htu</c> + <c>iat</c> binding
/// confines any RS-side reuse to a single endpoint within the short freshness
/// window. An opt-in RS replay cache can be layered on later.</para>
/// </summary>
internal static class DpopResourceValidator
{
    public static DpopResourceResult Validate(
        HttpRequest request, string accessToken, string expectedJkt, DateTimeOffset now)
    {
        var header = request.Headers["DPoP"];
        if (header.Count == 0)
            return DpopResourceResult.NoProof;
        if (header.Count > 1)
            return DpopResourceResult.InvalidProof;

        // The request URL as this server sees it. Behind a reverse proxy the host
        // must be forwarded correctly (UseForwardedHeaders) for htu to match what
        // the client signed.
        var htu = $"{request.Scheme}://{request.Host}{request.Path}";

        var result = DpopProofValidator.Validate(
            header.ToString(), request.Method, htu, now, accessToken: accessToken);
        if (!result.IsValid)
            return DpopResourceResult.InvalidProof;

        return string.Equals(result.Jkt, expectedJkt, StringComparison.Ordinal)
            ? DpopResourceResult.Valid
            : DpopResourceResult.ThumbprintMismatch;
    }
}

/// <summary>Shared DPoP identifiers for the resource-server side.</summary>
internal static class DpopResource
{
    /// <summary>The <c>Authorization</c> scheme a DPoP-bound token is presented under.</summary>
    public const string Scheme = "DPoP";

    /// <summary>Internal claim carrying the token's <c>cnf.jkt</c> thumbprint, surfaced
    /// from the introspection response so the handler can enforce the binding.</summary>
    public const string ConfirmationJktClaimType = "modgud:dpop:cnf-jkt";
}
