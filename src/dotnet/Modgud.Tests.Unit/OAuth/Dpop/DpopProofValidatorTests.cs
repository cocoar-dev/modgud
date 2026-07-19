using Modgud.Infrastructure.OpenIddict.Dpop;

namespace Modgud.Tests.Unit.OAuth.Dpop;

/// <summary>
/// Behavioural tests for the DPoP proof validator (RFC 9449 §4.3). Proofs are
/// minted with real signatures by <see cref="DpopKey"/>; rejection cases either
/// ask the factory to omit a claim or tamper with a valid proof's bytes, so each
/// test isolates exactly one failure reason.
/// </summary>
public class DpopProofValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Htu = "https://rs.example.test/resource";
    private const string Htm = "POST";

    [Theory]
    [InlineData("ES256")]
    [InlineData("ES384")]
    [InlineData("ES512")]
    [InlineData("RS256")]
    [InlineData("PS256")]
    public void Accepts_a_valid_proof_and_returns_the_binding_thumbprint(string alg)
    {
        using var key = alg.StartsWith("ES") ? DpopKey.CreateEc(alg) : DpopKey.CreateRsa(alg);
        var proof = key.CreateProof(Now, Htm, Htu, jti: "unique-1");

        var result = DpopProofValidator.Validate(proof, Htm, Htu, Now);

        Assert.True(result.IsValid, $"expected valid, got {result.Error}");
        Assert.Equal(key.Jkt, result.Jkt);
        Assert.Equal("unique-1", result.Jti);
        Assert.Equal(Now, result.IssuedAt);
    }

    [Fact]
    public void Accepts_a_resource_server_proof_whose_ath_matches_the_access_token()
    {
        using var key = DpopKey.CreateEc();
        const string token = "opaque-access-token-value";
        var proof = key.CreateProof(Now, "GET", Htu, ath: DpopKey.ComputeAth(token));

        var result = DpopProofValidator.Validate(proof, "GET", Htu, Now, accessToken: token);

        Assert.True(result.IsValid, $"expected valid, got {result.Error}");
        Assert.Equal(key.Jkt, result.Jkt);
    }

    [Fact]
    public void Rejects_when_ath_does_not_match_the_presented_token()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, "GET", Htu, ath: DpopKey.ComputeAth("some-other-token"));

        var result = DpopProofValidator.Validate(proof, "GET", Htu, Now, accessToken: "the-real-token");

        Assert.Equal(DpopError.AccessTokenHashMismatch, result.Error);
    }

    [Fact]
    public void Normalises_htu_default_port_host_case_and_query()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, Htm, "https://RS.Example.Test:443/resource?foo=bar#frag");

        var result = DpopProofValidator.Validate(proof, Htm, "https://rs.example.test/resource", Now);

        Assert.True(result.IsValid, $"expected valid, got {result.Error}");
    }

    [Fact]
    public void Rejects_a_missing_proof() =>
        Assert.Equal(DpopError.Missing, DpopProofValidator.Validate(null, Htm, Htu, Now).Error);

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.???.$$$")]
    public void Rejects_a_structurally_malformed_proof(string proof) =>
        Assert.Equal(DpopError.Malformed, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);

    [Fact]
    public void Rejects_a_wrong_typ_header()
    {
        using var key = DpopKey.CreateEc();
        var proof = DpopProofTamper.ReplaceInHeader(key.CreateProof(Now, Htm, Htu), "dpop+jwt", "jwt");

        Assert.Equal(DpopError.InvalidType, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_non_asymmetric_algorithm()
    {
        using var key = DpopKey.CreateEc();
        // alg check runs before signature verification, so the now-broken
        // signature is irrelevant — we're asserting the allowlist gate.
        var proof = DpopProofTamper.ReplaceInHeader(key.CreateProof(Now, Htm, Htu), "ES256", "HS256");

        Assert.Equal(DpopError.UnsupportedAlgorithm, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_proof_carrying_private_key_material()
    {
        using var key = DpopKey.CreateEc();
        // Inject a private "d" member into the jwk header.
        var proof = DpopProofTamper.ReplaceInHeader(
            key.CreateProof(Now, Htm, Htu), "\"kty\":\"EC\"", "\"kty\":\"EC\",\"d\":\"AAAA\"");

        Assert.Equal(DpopError.ContainsPrivateKey, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_tampered_signature()
    {
        using var key = DpopKey.CreateEc();
        var proof = DpopProofTamper.CorruptSignature(key.CreateProof(Now, Htm, Htu));

        Assert.Equal(DpopError.InvalidSignature, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_proof_signed_by_a_different_key_than_its_embedded_jwk()
    {
        // Splice key B's signature onto a proof whose header advertises key A's
        // public jwk: the signature can't verify against the advertised key.
        using var keyA = DpopKey.CreateEc();
        using var keyB = DpopKey.CreateEc();
        var proofA = keyA.CreateProof(Now, Htm, Htu);
        var proofB = keyB.CreateProof(Now, Htm, Htu);
        var partsA = proofA.Split('.');
        var partsB = proofB.Split('.');
        var spliced = $"{partsA[0]}.{partsA[1]}.{partsB[2]}";

        Assert.Equal(DpopError.InvalidSignature, DpopProofValidator.Validate(spliced, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_method_mismatch()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, "GET", Htu);

        Assert.Equal(DpopError.MethodMismatch, DpopProofValidator.Validate(proof, "POST", Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_uri_mismatch()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, Htm, "https://rs.example.test/other");

        Assert.Equal(DpopError.UriMismatch, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_stale_proof()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now.AddMinutes(-5), Htm, Htu);

        Assert.Equal(DpopError.Expired, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Rejects_a_proof_from_the_future()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now.AddMinutes(5), Htm, Htu);

        Assert.Equal(DpopError.FutureProof, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Theory]
    [InlineData(true, false, false, false)]  // omit jti
    [InlineData(false, true, false, false)]  // omit htm
    [InlineData(false, false, true, false)]  // omit htu
    [InlineData(false, false, false, true)]  // omit iat
    public void Rejects_a_proof_missing_a_required_claim(bool omitJti, bool omitHtm, bool omitHtu, bool omitIat)
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(
            Now,
            htm: omitHtm ? null : Htm,
            htu: omitHtu ? null : Htu,
            omitJti: omitJti,
            omitIat: omitIat);

        Assert.Equal(DpopError.MissingClaim, DpopProofValidator.Validate(proof, Htm, Htu, Now).Error);
    }

    [Fact]
    public void Accepts_a_proof_carrying_the_required_server_nonce()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, Htm, Htu, nonce: "server-nonce-abc");

        var result = DpopProofValidator.Validate(proof, Htm, Htu, Now, expectedNonce: "server-nonce-abc");

        Assert.True(result.IsValid, $"expected valid, got {result.Error}");
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData(null)]
    public void Rejects_when_a_required_nonce_is_absent_or_wrong(string? proofNonce)
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, Htm, Htu, nonce: proofNonce);

        var result = DpopProofValidator.Validate(proof, Htm, Htu, Now, expectedNonce: "server-nonce-abc");

        Assert.Equal(DpopError.NonceMismatch, result.Error);
    }
}
