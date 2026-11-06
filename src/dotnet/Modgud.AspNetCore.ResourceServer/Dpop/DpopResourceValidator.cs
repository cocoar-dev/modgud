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
    /// <summary>SDK-B5-02 — the proof is valid but was already presented once.</summary>
    Replayed,
}

/// <summary>
/// Resource-server half of DPoP (RFC 9449 §7.2): confirms that the client
/// presenting a sender-constrained access token actually holds the bound key.
/// It validates the request's <c>DPoP</c> proof against the HTTP method + URL and
/// the access token's hash (<c>ath</c>), then checks the proof key's RFC 7638
/// thumbprint equals the token's <c>cnf.jkt</c>.
///
/// <para>Clock injected. SDK-B5-02 — with an <see cref="IDpopReplayCache"/> (registered by
/// default) a proof is accepted once: its <c>jti</c> is remembered for the freshness window,
/// and the same proof presented again is <see cref="DpopResourceResult.Replayed"/>.</para>
/// </summary>
internal static class DpopResourceValidator
{
    public static DpopResourceResult Validate(
        HttpRequest request, string accessToken, string expectedJkt, DateTimeOffset now,
        IDpopReplayCache? replayCache = null)
    {
        var header = request.Headers["DPoP"];
        if (header.Count == 0)
            return DpopResourceResult.NoProof;
        if (header.Count > 1)
            return DpopResourceResult.InvalidProof;

        // The request URL as this server sees it. Behind a reverse proxy the host
        // must be forwarded correctly (UseForwardedHeaders) for htu to match what
        // the client signed. SDK-B5-03 — PathBase is part of that URL: under a mount
        // (UsePathBase, a proxy prefix) the client signs /api/data, not /data.
        var htu = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";

        var result = DpopProofValidator.Validate(
            header.ToString(), request.Method, htu, now, accessToken: accessToken);
        if (!result.IsValid)
            return DpopResourceResult.InvalidProof;

        if (!string.Equals(result.Jkt, expectedJkt, StringComparison.Ordinal))
            return DpopResourceResult.ThumbprintMismatch;

        // Record only a proof that passed every other check, so a forged or foreign proof
        // can never burn a legitimate client's jti. The window ends when the validator would
        // reject the proof as stale anyway.
        if (replayCache is not null && result.Jti is { Length: > 0 } jti && result.IssuedAt is { } issuedAt)
        {
            var notAfter = issuedAt + DpopProofValidator.DefaultMaxAge + DpopProofValidator.DefaultClockSkew;
            if (!replayCache.TryUse(expectedJkt, jti, notAfter, now))
                return DpopResourceResult.Replayed;
        }

        return DpopResourceResult.Valid;
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
