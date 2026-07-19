using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Modgud.Infrastructure.OpenIddict.Dpop;

namespace Modgud.Tests.Unit.OAuth.Dpop;

/// <summary>
/// Cross-checks our RFC 7638 thumbprint against an INDEPENDENT implementation —
/// Microsoft.IdentityModel.Tokens' <c>JsonWebKey.ComputeJwkThumbprint()</c> — for
/// both key families DPoP uses. Two implementations agreeing on random keys is a
/// far stronger correctness signal than a single hard-coded vector, and it guards
/// the subtle bits (RSA minimal-octet trimming, EC fixed-width padding, member
/// ordering) that a naive thumbprint gets wrong.
/// </summary>
public class JwkThumbprintTests
{
    [Fact]
    public void Rsa_thumbprint_matches_identity_model_oracle()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);

        var oracle = Base64UrlEncoder.Encode(
            JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa)).ComputeJwkThumbprint());

        Assert.Equal(oracle, JwkThumbprint.ForRsa(p.Modulus!, p.Exponent!));
    }

    [Theory]
    [InlineData("P-256")]
    [InlineData("P-384")]
    [InlineData("P-521")]
    public void Ec_thumbprint_matches_identity_model_oracle(string crv)
    {
        var curve = crv switch
        {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521,
        };
        using var ec = ECDsa.Create(curve);
        var p = ec.ExportParameters(false);

        var oracle = Base64UrlEncoder.Encode(
            JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ec)).ComputeJwkThumbprint());

        Assert.Equal(oracle, JwkThumbprint.ForEc(crv, p.Q.X!, p.Q.Y!));
    }

    [Fact]
    public void Same_rsa_key_yields_a_stable_thumbprint()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        Assert.Equal(
            JwkThumbprint.ForRsa(p.Modulus!, p.Exponent!),
            JwkThumbprint.ForRsa(p.Modulus!, p.Exponent!));
    }

    [Fact]
    public void Different_keys_yield_different_thumbprints()
    {
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pa = a.ExportParameters(false);
        var pb = b.ExportParameters(false);
        Assert.NotEqual(
            JwkThumbprint.ForEc("P-256", pa.Q.X!, pa.Q.Y!),
            JwkThumbprint.ForEc("P-256", pb.Q.X!, pb.Q.Y!));
    }

    [Fact]
    public void A_stray_leading_zero_on_the_rsa_modulus_does_not_change_the_thumbprint()
    {
        // The canonicalisation must trim to minimal octets (RFC 7518 §6.3.1);
        // otherwise the issuance-time and validation-time proofs — which may
        // encode the same key slightly differently — would bind to different jkts.
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var padded = new byte[p.Modulus!.Length + 1];
        Array.Copy(p.Modulus!, 0, padded, 1, p.Modulus!.Length); // leading 0x00

        Assert.Equal(
            JwkThumbprint.ForRsa(p.Modulus!, p.Exponent!),
            JwkThumbprint.ForRsa(padded, p.Exponent!));
    }

    [Fact]
    public void Unsupported_curve_throws()
    {
        Assert.Throws<ArgumentException>(() => JwkThumbprint.ForEc("P-192", new byte[24], new byte[24]));
    }
}
