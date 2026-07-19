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

namespace Modgud.Client.AspNetCore.Dpop;

/// <summary>
/// RFC 7638 JWK thumbprint (SHA-256, base64url) for the two key types DPoP
/// proofs actually use: RSA (RS*/PS* proofs) and EC (ES* proofs). This is the
/// value that becomes the <c>cnf.jkt</c> confirmation claim binding an access
/// token to the client's proof-of-possession key (RFC 9449 §6).
///
/// <para>
/// The thumbprint is computed over a canonical JSON object containing ONLY the
/// required members in lexicographic order, with no whitespace (RFC 7638 §3):
/// <list type="bullet">
///   <item><description>RSA: <c>{"e":"…","kty":"RSA","n":"…"}</c></description></item>
///   <item><description>EC:  <c>{"crv":"…","kty":"EC","x":"…","y":"…"}</c></description></item>
/// </list>
/// We deliberately canonicalise the parameter encodings ourselves rather than
/// hashing the client-supplied base64url strings verbatim: RSA <c>n</c>/<c>e</c>
/// are trimmed to the minimum octets (RFC 7518 §6.3.1), EC <c>x</c>/<c>y</c> are
/// left-zero-padded to the curve's fixed coordinate length (RFC 7518 §6.2.1.2).
/// Without this, two proofs carrying the SAME key but a cosmetically different
/// encoding (e.g. a stray leading zero byte) would hash to different thumbprints
/// and the issuance-time <c>cnf.jkt</c> would never match the validation-time
/// proof — silently breaking the binding.
/// </para>
///
/// <para>
/// Kept dependency-free (BCL only: <see cref="System.Buffers.Text.Base64Url"/> +
/// <see cref="System.Security.Cryptography"/>) so the exact same file can be
/// duplicated verbatim into the dependency-light <c>Modgud.Client.AspNetCore</c>
/// NuGet for resource-server-side validation. Any change here MUST be mirrored
/// there — see the "keep in sync" note on the client copy.
/// </para>
/// </summary>
public static class JwkThumbprint
{
    /// <summary>
    /// RFC 7638 thumbprint of an RSA public key from its raw modulus / exponent
    /// octets (as decoded from a JWK's <c>n</c> / <c>e</c> members).
    /// </summary>
    public static string ForRsa(byte[] modulus, byte[] exponent)
    {
        ArgumentNullException.ThrowIfNull(modulus);
        ArgumentNullException.ThrowIfNull(exponent);

        var n = Base64Url.EncodeToString(TrimLeadingZeros(modulus));
        var e = Base64Url.EncodeToString(TrimLeadingZeros(exponent));
        return Sha256Base64Url($"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}");
    }

    /// <summary>
    /// RFC 7638 thumbprint of an EC public key from its curve name and raw
    /// coordinate octets (as decoded from a JWK's <c>crv</c> / <c>x</c> / <c>y</c>
    /// members). Only the NIST curves DPoP uses (P-256/P-384/P-521) are accepted;
    /// an unknown <paramref name="crv"/> throws.
    /// </summary>
    public static string ForEc(string crv, byte[] x, byte[] y)
    {
        ArgumentException.ThrowIfNullOrEmpty(crv);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var length = CoordinateLength(crv);
        var xEnc = Base64Url.EncodeToString(LeftPad(x, length));
        var yEnc = Base64Url.EncodeToString(LeftPad(y, length));
        return Sha256Base64Url($"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{xEnc}\",\"y\":\"{yEnc}\"}}");
    }

    /// <summary>
    /// Fixed coordinate byte length for the supported EC curves. EC coordinates
    /// are fixed-width per RFC 7518 §6.2.1.2 (unlike RSA integers), so they must
    /// be left-zero-padded to this length before encoding — never trimmed.
    /// </summary>
    public static int CoordinateLength(string crv) => crv switch
    {
        "P-256" => 32,
        "P-384" => 48,
        "P-521" => 66,
        _ => throw new ArgumentException($"Unsupported EC curve '{crv}'.", nameof(crv)),
    };

    private static string Sha256Base64Url(string canonicalJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Base64Url.EncodeToString(hash);
    }

    // RSA integers use the minimum number of octets (RFC 7518 §6.3.1): a leading
    // 0x00 sign byte or any zero padding must be stripped before encoding.
    private static byte[] TrimLeadingZeros(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length - 1 && bytes[i] == 0) i++;
        return i == 0 ? bytes : bytes[i..];
    }

    // EC coordinates are fixed-width: left-pad with zero octets to the curve
    // length. (An over-long input — more than length after its own leading
    // zeros — is a malformed key; we pad only, never truncate meaningful bytes.)
    private static byte[] LeftPad(byte[] bytes, int length)
    {
        if (bytes.Length == length) return bytes;
        if (bytes.Length > length)
        {
            // Tolerate an extra leading zero (some encoders add a sign byte).
            var trimmed = TrimLeadingZeros(bytes);
            if (trimmed.Length == length) return trimmed;
            if (trimmed.Length < length) bytes = trimmed;
            else throw new ArgumentException("EC coordinate longer than the curve length.", nameof(bytes));
        }
        var padded = new byte[length];
        Array.Copy(bytes, 0, padded, length - bytes.Length, bytes.Length);
        return padded;
    }
}
