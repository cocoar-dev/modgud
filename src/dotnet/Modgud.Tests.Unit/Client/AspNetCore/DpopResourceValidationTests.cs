using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Client.AspNetCore;
using Modgud.Client.AspNetCore.Dpop;
using Modgud.Tests.Unit.OAuth.Dpop;

namespace Modgud.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Resource-server side of DPoP in the client library: surfacing <c>cnf.jkt</c>
/// from an introspection response and validating a request's proof against the
/// bound key + the presented token (RFC 9449 §7.2).
/// </summary>
public class DpopResourceValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Audience = "https://rs.example.test";
    private const string ResourceUrl = "https://rs.example.test/data";
    private const string Token = "opaque-reference-token-value";

    // ── introspection cnf surfacing ───────────────────────────────────────────

    [Fact]
    public void BuildPrincipal_surfaces_cnf_jkt_from_the_introspection_response()
    {
        using var key = DpopKey.CreateEc();
        var body = JsonSerializer.Serialize(new
        {
            active = true,
            aud = Audience,
            sub = "u1",
            cnf = new { jkt = key.Jkt },
        });

        var principal = ModgudTokenIntrospection.BuildPrincipal(body, Audience, "Test", NullLogger.Instance);

        Assert.NotNull(principal);
        Assert.Equal(key.Jkt, principal!.FindFirst(DpopResource.ConfirmationJktClaimType)?.Value);
    }

    [Fact]
    public void BuildPrincipal_adds_no_cnf_claim_for_an_unbound_token()
    {
        var body = JsonSerializer.Serialize(new { active = true, aud = Audience, sub = "u1" });

        var principal = ModgudTokenIntrospection.BuildPrincipal(body, Audience, "Test", NullLogger.Instance);

        Assert.NotNull(principal);
        Assert.Null(principal!.FindFirst(DpopResource.ConfirmationJktClaimType));
    }

    // ── request-time proof validation ─────────────────────────────────────────

    [Fact]
    public void Validate_accepts_a_matching_proof()
    {
        using var key = DpopKey.CreateEc();
        var request = RequestWithProof(key.CreateProof(
            Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth(Token)));

        Assert.Equal(DpopResourceResult.Valid,
            DpopResourceValidator.Validate(request, Token, key.Jkt, Now));
    }

    [Fact]
    public void Validate_reports_no_proof_when_the_header_is_absent()
    {
        using var key = DpopKey.CreateEc();
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Scheme = "https";
        request.Host = new HostString("rs.example.test");
        request.Path = "/data";

        Assert.Equal(DpopResourceResult.NoProof,
            DpopResourceValidator.Validate(request, Token, key.Jkt, Now));
    }

    [Fact]
    public void Validate_rejects_a_proof_bound_to_a_different_url()
    {
        using var key = DpopKey.CreateEc();
        var request = RequestWithProof(key.CreateProof(
            Now, "GET", "https://rs.example.test/other", ath: DpopKey.ComputeAth(Token)));

        Assert.Equal(DpopResourceResult.InvalidProof,
            DpopResourceValidator.Validate(request, Token, key.Jkt, Now));
    }

    [Fact]
    public void Validate_rejects_a_proof_whose_ath_does_not_match_the_token()
    {
        using var key = DpopKey.CreateEc();
        var request = RequestWithProof(key.CreateProof(
            Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth("a-different-token")));

        Assert.Equal(DpopResourceResult.InvalidProof,
            DpopResourceValidator.Validate(request, Token, key.Jkt, Now));
    }

    [Fact]
    public void Validate_rejects_a_proof_whose_key_does_not_match_the_binding()
    {
        using var boundKey = DpopKey.CreateEc();
        using var attackerKey = DpopKey.CreateEc();
        // A valid proof, but signed by a DIFFERENT key than the token is bound to.
        var request = RequestWithProof(attackerKey.CreateProof(
            Now, "GET", ResourceUrl, ath: DpopKey.ComputeAth(Token)));

        Assert.Equal(DpopResourceResult.ThumbprintMismatch,
            DpopResourceValidator.Validate(request, Token, boundKey.Jkt, Now));
    }

    private static HttpRequest RequestWithProof(string proof)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Scheme = "https";
        request.Host = new HostString("rs.example.test");
        request.Path = "/data";
        request.Headers["DPoP"] = proof;
        return request;
    }
}
