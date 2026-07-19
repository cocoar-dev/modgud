using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Infrastructure.OpenIddict.Dpop;

namespace Modgud.Tests.Unit.OAuth.Dpop;

/// <summary>
/// Mints real, correctly-signed DPoP proof JWTs for the validator tests — and,
/// later, for the PAR-style integration tests. A single <see cref="DpopKey"/>
/// wraps one keypair so multiple proofs share the same <see cref="Jkt"/>
/// (mirroring a client that reuses its session key across requests). Negative
/// cases are produced by the tamper helpers below rather than by weakening this
/// factory, so the "happy path" here always emits a spec-valid proof.
/// </summary>
internal sealed class DpopKey : IDisposable
{
    private readonly ECDsa? _ec;
    private readonly RSA? _rsa;
    private readonly Dictionary<string, object> _jwk;

    public string Alg { get; }
    public string Jkt { get; }

    private DpopKey(string alg, ECDsa? ec, RSA? rsa, Dictionary<string, object> jwk, string jkt)
    {
        Alg = alg;
        _ec = ec;
        _rsa = rsa;
        _jwk = jwk;
        Jkt = jkt;
    }

    public static DpopKey CreateEc(string alg = "ES256")
    {
        var (curve, crv) = alg switch
        {
            "ES256" => (ECCurve.NamedCurves.nistP256, "P-256"),
            "ES384" => (ECCurve.NamedCurves.nistP384, "P-384"),
            "ES512" => (ECCurve.NamedCurves.nistP521, "P-521"),
            _ => throw new ArgumentException($"Not an EC alg: {alg}", nameof(alg)),
        };
        var ec = ECDsa.Create(curve);
        var p = ec.ExportParameters(false);
        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "EC",
            ["crv"] = crv,
            ["x"] = B64(p.Q.X!),
            ["y"] = B64(p.Q.Y!),
        };
        return new DpopKey(alg, ec, null, jwk, JwkThumbprint.ForEc(crv, p.Q.X!, p.Q.Y!));
    }

    public static DpopKey CreateRsa(string alg = "RS256")
    {
        var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "RSA",
            ["n"] = B64(p.Modulus!),
            ["e"] = B64(p.Exponent!),
        };
        return new DpopKey(alg, null, rsa, jwk, JwkThumbprint.ForRsa(p.Modulus!, p.Exponent!));
    }

    /// <summary>
    /// Build a signed proof. Passing <c>null</c> for <paramref name="htm"/> /
    /// <paramref name="htu"/>, or setting <paramref name="omitJti"/> /
    /// <paramref name="omitIat"/>, produces a spec-valid signature over a proof
    /// that is missing that claim — exactly what the MissingClaim tests need.
    /// </summary>
    public string CreateProof(
        DateTimeOffset iat,
        string? htm = "POST",
        string? htu = "https://rs.example.test/resource",
        string? jti = null,
        string? ath = null,
        string? nonce = null,
        bool omitJti = false,
        bool omitIat = false)
    {
        var payload = new Dictionary<string, object>();
        if (!omitJti) payload["jti"] = jti ?? Guid.NewGuid().ToString("N");
        if (htm is not null) payload["htm"] = htm;
        if (htu is not null) payload["htu"] = htu;
        if (!omitIat) payload["iat"] = iat.ToUnixTimeSeconds();
        if (ath is not null) payload["ath"] = ath;
        if (nonce is not null) payload["nonce"] = nonce;

        var header = new Dictionary<string, object>
        {
            ["typ"] = "dpop+jwt",
            ["alg"] = Alg,
            ["jwk"] = _jwk,
        };

        var signingInput = $"{Seg(header)}.{Seg(payload)}";
        var sig = Sign(Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{B64(sig)}";
    }

    /// <summary>The DPoP <c>ath</c> value for a given access token.</summary>
    public static string ComputeAth(string accessToken) =>
        B64(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

    private byte[] Sign(byte[] input)
    {
        var hash = Alg[^3..] switch
        {
            "256" => HashAlgorithmName.SHA256,
            "384" => HashAlgorithmName.SHA384,
            "512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
        if (_ec is not null)
            return _ec.SignData(input, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var padding = Alg[0] == 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        return _rsa!.SignData(input, hash, padding);
    }

    // Relaxed escaping so `typ:"dpop+jwt"` keeps a literal '+' (the STJ default
    // encoder emits `+`) — matches what real DPoP libraries produce and lets
    // the text-based tamper helpers find the header claims.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Seg(object o) => B64(JsonSerializer.SerializeToUtf8Bytes(o, JsonOptions));
    private static string B64(byte[] b) => Base64Url.EncodeToString(b);

    public void Dispose()
    {
        _ec?.Dispose();
        _rsa?.Dispose();
    }
}

/// <summary>Structural tamper helpers for the negative-path validator tests.</summary>
internal static class DpopProofTamper
{
    public static string ReplaceInHeader(string proof, string find, string replace) =>
        MutateSegment(proof, 0, json => json.Replace(find, replace));

    public static string CorruptSignature(string proof)
    {
        var parts = proof.Split('.');
        var sig = Base64Url.DecodeFromChars(parts[2]);
        sig[0] ^= 0xFF;
        parts[2] = Base64Url.EncodeToString(sig);
        return string.Join('.', parts);
    }

    private static string MutateSegment(string proof, int index, Func<string, string> mutate)
    {
        var parts = proof.Split('.');
        var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[index]));
        parts[index] = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(mutate(json)));
        return string.Join('.', parts);
    }
}
