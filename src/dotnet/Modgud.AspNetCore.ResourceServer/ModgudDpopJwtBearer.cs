using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Modgud.AspNetCore.ResourceServer.Dpop;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>
/// Resource-server DPoP enforcement for the <b>JWT-bearer</b> validation path
/// (RFC 9449 §7.1), the JWT-token twin of the introspection-path enforcement in
/// <see cref="ModgudIntrospectionHandler"/>.
///
/// <para>Two hooks into the JwtBearer pipeline, wired by
/// <c>AddModgudResourceServer</c> in a JWT-capable mode:</para>
/// <list type="number">
///   <item><b>OnMessageReceived</b> (<see cref="ExtractDpopSchemeToken"/>) — a
///   DPoP-bound token is presented under the <c>DPoP</c> auth scheme, not
///   <c>Bearer</c>. JwtBearer only reads <c>Authorization: Bearer …</c>, so the
///   token has to be lifted out of the <c>DPoP</c> header explicitly or the JWT
///   never gets validated at all.</item>
///   <item><b>OnTokenValidated</b> (<see cref="EnforceBinding"/>) — once the JWT
///   is validated, a token carrying a <c>cnf.jkt</c> confirmation claim MUST be
///   accompanied by a valid DPoP proof whose key matches. A bound token presented
///   as a plain bearer is rejected; the <c>DPoP</c> scheme used against an unbound
///   token is rejected too (the client is asserting a possession the token doesn't
///   carry).</item>
/// </list>
///
/// <para>The actual proof cryptography lives in the shared, dependency-light
/// <see cref="DpopResourceValidator"/> / <see cref="DpopProofValidator"/> core —
/// identical to the introspection path. RS-side <c>jti</c> replay tracking is
/// out of scope for the same reason as there (the AS rejects replays at issuance;
/// the <c>ath</c> + <c>htu</c> + <c>iat</c> binding confines any reuse to one
/// endpoint within the freshness window).</para>
/// </summary>
internal static class ModgudDpopJwtBearer
{
    /// <summary>The confirmation claim a validated JWT carries when it is
    /// DPoP-bound: <c>cnf = {"jkt": "&lt;thumbprint&gt;"}</c> (RFC 9449 §6 / RFC 7800).
    /// <see cref="Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler"/> maps
    /// the nested object to a single <c>cnf</c> claim whose value is the raw JSON.</summary>
    private const string ConfirmationClaimType = "cnf";

    /// <summary>The thumbprint member inside <c>cnf</c> (RFC 9449 §6). Inlined
    /// rather than referenced from the server-side <c>DpopConstants</c> to keep
    /// this published NuGet dependency-light.</summary>
    private const string JwkThumbprintMember = "jkt";

    /// <summary>
    /// Pulls the access token out of an <c>Authorization: DPoP &lt;token&gt;</c>
    /// header so JwtBearer can validate the JWT it carries. Returns <c>null</c> for
    /// a missing header, a <c>Bearer</c> (or any non-DPoP) scheme, or an empty
    /// parameter — in those cases JwtBearer's own extraction is left untouched.
    /// </summary>
    public static string? ExtractDpopSchemeToken(HttpRequest request)
    {
        var raw = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw) ||
            !AuthenticationHeaderValue.TryParse(raw, out var header) ||
            !string.Equals(header.Scheme, DpopResource.Scheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return null;
        }

        return header.Parameter;
    }

    /// <summary>
    /// Reads the RFC 7638 thumbprint out of a validated principal's <c>cnf</c>
    /// claim (<c>{"jkt": "…"}</c>). Returns <c>null</c> for an unbound token
    /// (no <c>cnf</c>, or a <c>cnf</c> without a string <c>jkt</c>).
    /// </summary>
    public static string? TryGetBoundJkt(ClaimsPrincipal? principal)
    {
        var cnf = principal?.FindFirst(ConfirmationClaimType)?.Value;
        if (string.IsNullOrEmpty(cnf)) return null;

        try
        {
            using var doc = JsonDocument.Parse(cnf);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(JwkThumbprintMember, out var jkt) &&
                jkt.ValueKind == JsonValueKind.String)
            {
                return jkt.GetString();
            }
        }
        catch (JsonException)
        {
            // A malformed cnf is treated as "no binding to enforce" — the token
            // simply won't be honoured as DPoP-bound. It can't be forged into a
            // binding, which is the only security-relevant direction.
        }

        return null;
    }

    /// <summary>Why <see cref="EvaluateBinding"/> accepted or rejected a request.</summary>
    public enum BindingResult
    {
        /// <summary>Either an unbound token under a non-DPoP scheme, or a bound
        /// token presented under DPoP with a valid, matching proof.</summary>
        Ok,
        /// <summary>The token is DPoP-bound but was presented without the DPoP scheme.</summary>
        BoundButNotDpopScheme,
        /// <summary>The token is DPoP-bound and used the DPoP scheme, but the proof
        /// was missing, invalid, stale, bound to another request/token, or signed
        /// by a key whose thumbprint ≠ the token's <c>cnf.jkt</c>.</summary>
        ProofInvalid,
        /// <summary>The DPoP scheme was used against a token that is not bound.</summary>
        DpopSchemeButUnbound,
    }

    /// <summary>
    /// Pure binding decision (RFC 9449 §7.1), factored out of <see cref="EnforceBinding"/>
    /// so every branch is unit-testable without standing up the auth pipeline.
    /// <paramref name="now"/> is injected for the proof's temporal checks.
    /// </summary>
    public static BindingResult EvaluateBinding(HttpRequest request, ClaimsPrincipal? principal, DateTimeOffset now)
    {
        var boundJkt = TryGetBoundJkt(principal);

        var raw = request.Headers.Authorization.ToString();
        var isDpop = !string.IsNullOrEmpty(raw) &&
            AuthenticationHeaderValue.TryParse(raw, out var header) &&
            string.Equals(header.Scheme, DpopResource.Scheme, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(header.Parameter);

        if (!string.IsNullOrEmpty(boundJkt))
        {
            if (!isDpop)
                return BindingResult.BoundButNotDpopScheme;

            // Re-parse to get the token parameter for the ath check. isDpop above
            // guarantees a well-formed DPoP header with a non-empty parameter.
            AuthenticationHeaderValue.TryParse(raw, out var dpopHeader);
            var outcome = DpopResourceValidator.Validate(request, dpopHeader!.Parameter!, boundJkt, now);
            return outcome == DpopResourceResult.Valid ? BindingResult.Ok : BindingResult.ProofInvalid;
        }

        // Unbound token: honour it as an ordinary bearer token, but reject a
        // client that dresses it up as DPoP — it's asserting a binding the token
        // doesn't have.
        return isDpop ? BindingResult.DpopSchemeButUnbound : BindingResult.Ok;
    }

    /// <summary>
    /// OnTokenValidated hook: runs <see cref="EvaluateBinding"/> and, on any
    /// rejection, fails the authentication (→ 401) with an RFC-flavoured reason.
    /// A pass leaves the context untouched so scheme-local claims projection
    /// can continue.
    /// </summary>
    public static void EnforceBinding(TokenValidatedContext context)
    {
        switch (EvaluateBinding(context.HttpContext.Request, context.Principal, DateTimeOffset.UtcNow))
        {
            case BindingResult.BoundButNotDpopScheme:
                context.Fail("This access token is DPoP-bound and must be presented with the DPoP scheme.");
                break;
            case BindingResult.ProofInvalid:
                context.Fail("The DPoP proof presented with this access token did not validate.");
                break;
            case BindingResult.DpopSchemeButUnbound:
                context.Fail("The DPoP scheme was used but the access token is not DPoP-bound.");
                break;
            case BindingResult.Ok:
                break;
        }
    }
}
