// ─────────────────────────────────────────────────────────────────────────
// DUPLICATED, KEEP IN SYNC. Verbatim copy (namespace aside) of
// Modgud.Infrastructure/OpenIddict/Dpop/<same file>. The resource-server side
// needs the identical DPoP crypto, but this client library is a published NuGet
// kept deliberately dependency-light, so the code is duplicated rather than
// shared. Any change to the server-side original MUST be mirrored here.
// ─────────────────────────────────────────────────────────────────────────

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modgud.Client.AspNetCore.Dpop;

/// <summary>
/// Validates a DPoP proof JWT (RFC 9449 §4.3): a compact JWS sent in the
/// <c>DPoP</c> HTTP header, signed by the client with the private half of the
/// key whose public half it embeds in its own <c>jwk</c> header. The proof
/// binds a single HTTP request to that key by carrying the method (<c>htm</c>),
/// URI (<c>htu</c>), an issue time (<c>iat</c>), a unique id (<c>jti</c>), and —
/// when presented alongside an access token at a resource server — the token's
/// hash (<c>ath</c>).
///
/// <para>
/// This type is deliberately <b>pure and stateless</b>: it verifies structure,
/// the self-contained signature, and the temporal/binding claims, then returns
/// the computed <c>jkt</c> thumbprint. It does NOT touch a database or clock of
/// its own — <see cref="DateTimeOffset"/> <c>now</c> is injected, and
/// <c>jti</c> replay detection is left to the caller (it needs a per-realm store
/// and a TTL policy that live outside this crypto core). Keeping it side-effect
/// free is what lets the identical file be duplicated into the dependency-light
/// <c>Modgud.Client.AspNetCore</c> NuGet for the resource-server side.
/// </para>
///
/// <para>
/// BCL only (<see cref="System.Buffers.Text.Base64Url"/>,
/// <see cref="System.Security.Cryptography"/>, <see cref="System.Text.Json"/>).
/// Any change here MUST be mirrored on the client copy — see the "keep in sync"
/// note there. See also <see cref="JwkThumbprint"/>.
/// </para>
/// </summary>
public static class DpopProofValidator
{
    /// <summary>How old a proof's <c>iat</c> may be before it is rejected.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(60);

    /// <summary>Tolerance for a proof issued slightly in the future (clock skew).</summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromSeconds(5);

    // Upper bound on a proof's RSA modulus (8192-bit) — a DoS guard, see usage.
    private const int MaxRsaModulusBytes = 1024;

    // Asymmetric JWS algorithms only. "none" and all symmetric (HS*) algorithms
    // are intentionally excluded: a DPoP proof is self-signed by a key the client
    // reveals, so only public-key signatures make sense — and accepting "none"
    // would defeat the entire mechanism.
    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.Ordinal)
    {
        "RS256", "RS384", "RS512",
        "PS256", "PS384", "PS512",
        "ES256", "ES384", "ES512",
    };

    /// <summary>
    /// Validate a proof against the request it is supposed to authorise.
    /// </summary>
    /// <param name="proof">The raw <c>DPoP</c> header value.</param>
    /// <param name="htmExpected">The actual HTTP method (e.g. <c>POST</c>).</param>
    /// <param name="htuExpected">The actual request URI (query/fragment ignored).</param>
    /// <param name="now">Current time (injected for determinism/testing).</param>
    /// <param name="accessToken">
    /// When validating at a resource server, the presented access token — its hash
    /// is checked against the proof's <c>ath</c>. Null at the token endpoint (no
    /// access token exists yet), where <c>ath</c> is not expected.
    /// </param>
    /// <param name="expectedNonce">A server-issued nonce to require, or null.</param>
    /// <param name="maxAge">Overrides <see cref="DefaultMaxAge"/>.</param>
    /// <param name="clockSkew">Overrides <see cref="DefaultClockSkew"/>.</param>
    public static DpopValidationResult Validate(
        string? proof,
        string htmExpected,
        string htuExpected,
        DateTimeOffset now,
        string? accessToken = null,
        string? expectedNonce = null,
        TimeSpan? maxAge = null,
        TimeSpan? clockSkew = null)
    {
        if (string.IsNullOrEmpty(proof))
            return DpopValidationResult.Fail(DpopError.Missing);

        var parts = proof.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return DpopValidationResult.Fail(DpopError.Malformed);

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = ParseJsonSegment(parts[0]);
            payload = ParseJsonSegment(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch
        {
            return DpopValidationResult.Fail(DpopError.Malformed);
        }

        // ---- header: typ / alg / jwk ----
        if (!TryGetString(header, "typ", out var typ) || !string.Equals(typ, "dpop+jwt", StringComparison.Ordinal))
            return DpopValidationResult.Fail(DpopError.InvalidType);

        if (!TryGetString(header, "alg", out var alg) || !AllowedAlgorithms.Contains(alg))
            return DpopValidationResult.Fail(DpopError.UnsupportedAlgorithm);

        if (header.ValueKind != JsonValueKind.Object ||
            !header.TryGetProperty("jwk", out var jwk) ||
            jwk.ValueKind != JsonValueKind.Object)
            return DpopValidationResult.Fail(DpopError.Malformed);

        // A proof carries the PUBLIC key only. Any private member is malformed
        // at best and an attempt to smuggle key material at worst — reject.
        foreach (var priv in PrivateJwkMembers)
            if (jwk.TryGetProperty(priv, out _))
                return DpopValidationResult.Fail(DpopError.ContainsPrivateKey);

        // ---- signature + thumbprint ----
        string? jkt;
        DpopError sigError;
        try
        {
            sigError = VerifySignature(jwk, alg, parts[0], parts[1], signature, out jkt);
        }
        catch
        {
            // Bad key material (ImportParameters / DecodeFromChars throwing).
            return DpopValidationResult.Fail(DpopError.Malformed);
        }
        if (sigError != DpopError.None || jkt is null)
            return DpopValidationResult.Fail(sigError == DpopError.None ? DpopError.InvalidSignature : sigError);

        // ---- payload claims ----
        if (!TryGetString(payload, "jti", out var jti) || jti.Length == 0)
            return DpopValidationResult.Fail(DpopError.MissingClaim);

        if (!TryGetString(payload, "htm", out var htm) || htm.Length == 0)
            return DpopValidationResult.Fail(DpopError.MissingClaim);
        if (!string.Equals(htm, htmExpected, StringComparison.Ordinal))
            return DpopValidationResult.Fail(DpopError.MethodMismatch);

        if (!TryGetString(payload, "htu", out var htu) || htu.Length == 0)
            return DpopValidationResult.Fail(DpopError.MissingClaim);
        if (!HtuMatches(htu, htuExpected))
            return DpopValidationResult.Fail(DpopError.UriMismatch);

        if (!TryGetInt64(payload, "iat", out var iatUnix))
            return DpopValidationResult.Fail(DpopError.MissingClaim);
        var iat = DateTimeOffset.FromUnixTimeSeconds(iatUnix);
        var skew = clockSkew ?? DefaultClockSkew;
        var age = maxAge ?? DefaultMaxAge;
        if (iat > now + skew)
            return DpopValidationResult.Fail(DpopError.FutureProof);
        if (iat < now - age - skew)
            return DpopValidationResult.Fail(DpopError.Expired);

        if (expectedNonce is not null)
        {
            if (!TryGetString(payload, "nonce", out var nonce) || !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
                return DpopValidationResult.Fail(DpopError.NonceMismatch);
        }

        if (accessToken is not null)
        {
            var expectedAth = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
            if (!TryGetString(payload, "ath", out var ath) || !string.Equals(ath, expectedAth, StringComparison.Ordinal))
                return DpopValidationResult.Fail(DpopError.AccessTokenHashMismatch);
        }

        return DpopValidationResult.Success(jkt, jti, iat);
    }

    private static readonly string[] PrivateJwkMembers = { "d", "p", "q", "dp", "dq", "qi" };

    /// <summary>
    /// Verify the JWS signature over <c>header.payload</c> against the public key
    /// reconstructed from the proof's <c>jwk</c>, and compute the key's RFC 7638
    /// thumbprint. Returns <see cref="DpopError.None"/> + the thumbprint on
    /// success, otherwise the specific failure.
    /// </summary>
    private static DpopError VerifySignature(
        JsonElement jwk, string alg, string headerSegment, string payloadSegment, byte[] signature, out string? jkt)
    {
        jkt = null;
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        var hash = alg[^3..] switch
        {
            "256" => HashAlgorithmName.SHA256,
            "384" => HashAlgorithmName.SHA384,
            "512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

        if (!TryGetString(jwk, "kty", out var kty))
            return DpopError.Malformed;

        // RSA family (RS*/PS*).
        if (alg[0] is 'R' or 'P')
        {
            if (!string.Equals(kty, "RSA", StringComparison.Ordinal))
                return DpopError.UnsupportedAlgorithm;
            if (!TryGetString(jwk, "n", out var nB64) || !TryGetString(jwk, "e", out var eB64))
                return DpopError.Malformed;

            var n = Base64Url.DecodeFromChars(nB64);
            var e = Base64Url.DecodeFromChars(eB64);
            // Bound the modulus so a client can't force us to verify against an
            // absurdly large (e.g. 32k-bit) key on every request — a cheap DoS.
            // 1024 bytes = 8192-bit, comfortably above any legitimate DPoP key.
            if (n.Length is 0 or > MaxRsaModulusBytes)
                return DpopError.UnsupportedAlgorithm;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
            var padding = alg[0] == 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
            if (!rsa.VerifyData(signingInput, signature, hash, padding))
                return DpopError.InvalidSignature;

            jkt = JwkThumbprint.ForRsa(n, e);
            return DpopError.None;
        }

        // EC family (ES*). Pin the curve to the algorithm (ES256↔P-256, …) so a
        // cross-curve key can't be presented under a mismatched alg.
        if (!string.Equals(kty, "EC", StringComparison.Ordinal))
            return DpopError.UnsupportedAlgorithm;

        var expectedCrv = alg switch
        {
            "ES256" => "P-256",
            "ES384" => "P-384",
            "ES512" => "P-521",
            _ => null,
        };
        if (expectedCrv is null ||
            !TryGetString(jwk, "crv", out var crv) || !string.Equals(crv, expectedCrv, StringComparison.Ordinal) ||
            !TryGetString(jwk, "x", out var xB64) || !TryGetString(jwk, "y", out var yB64))
            return DpopError.UnsupportedAlgorithm;

        var curve = crv switch
        {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new InvalidOperationException(),
        };
        var x = Base64Url.DecodeFromChars(xB64);
        var y = Base64Url.DecodeFromChars(yB64);
        using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = new ECPoint { X = x, Y = y } });
        if (!ecdsa.VerifyData(signingInput, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return DpopError.InvalidSignature;

        jkt = JwkThumbprint.ForEc(crv, x, y);
        return DpopError.None;
    }

    private static JsonElement ParseJsonSegment(string segment)
    {
        var bytes = Base64Url.DecodeFromChars(segment);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }
        return false;
    }

    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object &&
               obj.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt64(out value);
    }

    // Compare two htu values after normalising to scheme://host[:non-default-port]/path,
    // dropping query and fragment (RFC 9449 §4.3: htu is the request URI without them).
    private static bool HtuMatches(string actual, string expected) =>
        TryNormalizeHtu(actual, out var a) &&
        TryNormalizeHtu(expected, out var e) &&
        string.Equals(a, e, StringComparison.Ordinal);

    private static bool TryNormalizeHtu(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        // Uri already lower-cases Scheme and Host; AbsolutePath excludes query/fragment.
        normalized = $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
        return true;
    }
}
