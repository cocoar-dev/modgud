using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Modgud.Client.AspNetCore;
using Modgud.Tests.Unit.OAuth.Dpop;

namespace Modgud.Tests.Unit.Client.AspNetCore;

/// <summary>
/// JWT-bearer path of DPoP in the client library (#118): lifting a DPoP-scheme
/// token into JwtBearer and enforcing the <c>cnf.jkt</c> binding on the validated
/// principal (RFC 9449 §7.1) — the JWT twin of the introspection path covered by
/// <see cref="DpopResourceValidationTests"/>.
/// </summary>
public class DpopJwtBearerBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    private const string ResourceUrl = "https://rs.example.test/data";
    private const string Token = "the-jwt-access-token-string";

    // ── token extraction (OnMessageReceived) ──────────────────────────────────

    [Fact]
    public void ExtractDpopSchemeToken_returns_the_token_for_a_dpop_scheme_header()
    {
        var request = Request("DPoP", Token);
        Assert.Equal(Token, ModgudDpopJwtBearer.ExtractDpopSchemeToken(request));
    }

    [Fact]
    public void ExtractDpopSchemeToken_ignores_a_bearer_scheme_header()
    {
        // Bearer is JwtBearer's own job — the lib must not steal it.
        var request = Request("Bearer", Token);
        Assert.Null(ModgudDpopJwtBearer.ExtractDpopSchemeToken(request));
    }

    [Fact]
    public void ExtractDpopSchemeToken_returns_null_when_there_is_no_authorization_header()
    {
        var request = new DefaultHttpContext().Request;
        Assert.Null(ModgudDpopJwtBearer.ExtractDpopSchemeToken(request));
    }

    // ── cnf.jkt surfacing ──────────────────────────────────────────────────────

    [Fact]
    public void TryGetBoundJkt_reads_the_thumbprint_from_a_cnf_claim()
    {
        Assert.Equal("abc123", ModgudDpopJwtBearer.TryGetBoundJkt(BoundPrincipal("abc123")));
    }

    [Fact]
    public void TryGetBoundJkt_is_null_for_an_unbound_principal()
    {
        Assert.Null(ModgudDpopJwtBearer.TryGetBoundJkt(UnboundPrincipal()));
    }

    [Fact]
    public void TryGetBoundJkt_is_null_when_cnf_carries_no_jkt()
    {
        // e.g. an mTLS-bound token (cnf.x5t#S256) — not a DPoP binding.
        var identity = new ClaimsIdentity("Test");
        identity.AddClaim(new Claim("cnf", "{\"x5t#S256\":\"zzz\"}"));
        Assert.Null(ModgudDpopJwtBearer.TryGetBoundJkt(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void TryGetBoundJkt_is_null_when_cnf_is_not_valid_json()
    {
        var identity = new ClaimsIdentity("Test");
        identity.AddClaim(new Claim("cnf", "not-json"));
        Assert.Null(ModgudDpopJwtBearer.TryGetBoundJkt(new ClaimsPrincipal(identity)));
    }

    // ── binding decision (OnTokenValidated core) ──────────────────────────────

    [Fact]
    public void EvaluateBinding_accepts_an_unbound_token_under_bearer()
    {
        Assert.Equal(ModgudDpopJwtBearer.BindingResult.Ok,
            ModgudDpopJwtBearer.EvaluateBinding(Request("Bearer", Token), UnboundPrincipal(), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_the_dpop_scheme_against_an_unbound_token()
    {
        // The client is asserting a possession the token doesn't carry.
        Assert.Equal(ModgudDpopJwtBearer.BindingResult.DpopSchemeButUnbound,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token), UnboundPrincipal(), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_a_bound_token_presented_as_a_plain_bearer()
    {
        using var key = DpopKey.CreateEc();
        Assert.Equal(ModgudDpopJwtBearer.BindingResult.BoundButNotDpopScheme,
            ModgudDpopJwtBearer.EvaluateBinding(Request("Bearer", Token), BoundPrincipal(key.Jkt), Now));
    }

    [Fact]
    public void EvaluateBinding_accepts_a_bound_token_with_a_valid_matching_proof()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth(Token));

        Assert.Equal(ModgudDpopJwtBearer.BindingResult.Ok,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token, proof), BoundPrincipal(key.Jkt), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_a_bound_dpop_request_with_no_proof_header()
    {
        using var key = DpopKey.CreateEc();
        Assert.Equal(ModgudDpopJwtBearer.BindingResult.ProofInvalid,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token), BoundPrincipal(key.Jkt), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_a_proof_bound_to_a_different_url()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, "GET", "https://rs.example.test/other", ath: DpopKey.ComputeAth(Token));

        Assert.Equal(ModgudDpopJwtBearer.BindingResult.ProofInvalid,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token, proof), BoundPrincipal(key.Jkt), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_a_proof_whose_ath_does_not_match_the_token()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth("a-different-token"));

        Assert.Equal(ModgudDpopJwtBearer.BindingResult.ProofInvalid,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token, proof), BoundPrincipal(key.Jkt), Now));
    }

    [Fact]
    public void EvaluateBinding_rejects_a_valid_proof_signed_by_the_wrong_key()
    {
        using var boundKey = DpopKey.CreateEc();
        using var attackerKey = DpopKey.CreateEc();
        // Structurally valid proof, but its key ≠ the token's cnf.jkt.
        var proof = attackerKey.CreateProof(Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth(Token));

        Assert.Equal(ModgudDpopJwtBearer.BindingResult.ProofInvalid,
            ModgudDpopJwtBearer.EvaluateBinding(Request("DPoP", Token, proof), BoundPrincipal(boundKey.Jkt), Now));
    }

    // ── EnforceBinding wiring (OnTokenValidated → Fail) ───────────────────────

    [Fact]
    public void EnforceBinding_fails_the_context_for_a_bound_token_presented_as_bearer()
    {
        using var key = DpopKey.CreateEc();
        var ctx = TokenValidatedContextFor(Request("Bearer", Token), BoundPrincipal(key.Jkt));

        ModgudDpopJwtBearer.EnforceBinding(ctx);

        Assert.NotNull(ctx.Result);
        Assert.NotNull(ctx.Result!.Failure);
    }

    [Fact]
    public void EnforceBinding_leaves_an_unbound_bearer_token_untouched()
    {
        var ctx = TokenValidatedContextFor(Request("Bearer", Token), UnboundPrincipal());

        ModgudDpopJwtBearer.EnforceBinding(ctx);

        Assert.Null(ctx.Result);
    }

    [Fact]
    public void EnforceBinding_passes_a_bound_token_with_a_valid_proof()
    {
        using var key = DpopKey.CreateEc();
        // EnforceBinding uses DateTimeOffset.UtcNow internally, so mint fresh.
        var now = DateTimeOffset.UtcNow;
        var proof = key.CreateProof(now, "GET", ResourceUrl, ath: DpopKey.ComputeAth(Token));
        var ctx = TokenValidatedContextFor(Request("DPoP", Token, proof), BoundPrincipal(key.Jkt));

        ModgudDpopJwtBearer.EnforceBinding(ctx);

        Assert.Null(ctx.Result);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static HttpRequest Request(string authScheme, string token, string? proof = null)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Scheme = "https";
        request.Host = new HostString("rs.example.test");
        request.Path = "/data";
        request.Headers.Authorization = $"{authScheme} {token}";
        if (proof is not null) request.Headers["DPoP"] = proof;
        return request;
    }

    private static ClaimsPrincipal BoundPrincipal(string jkt)
    {
        var identity = new ClaimsIdentity("Test");
        // Mirrors how JsonWebTokenHandler surfaces a nested cnf object: one claim
        // whose value is the raw JSON.
        identity.AddClaim(new Claim("cnf", JsonSerializer.Serialize(new { jkt })));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal UnboundPrincipal() => new(new ClaimsIdentity("Test"));

    private static TokenValidatedContext TokenValidatedContextFor(HttpRequest request, ClaimsPrincipal principal)
    {
        var scheme = new AuthenticationScheme("Bearer", displayName: null, handlerType: typeof(JwtBearerHandler));
        return new TokenValidatedContext(request.HttpContext, scheme, new JwtBearerOptions())
        {
            Principal = principal,
        };
    }
}
