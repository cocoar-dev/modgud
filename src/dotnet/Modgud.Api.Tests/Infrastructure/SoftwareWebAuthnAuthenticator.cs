using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// A minimal software WebAuthn (FIDO2) authenticator for integration tests. Holds
/// an ES256 (P-256) key pair, exposes its COSE-encoded public key (to seed a
/// <c>StoredPasskeyCredential</c>), and produces a fully valid, signed assertion
/// over a server-issued challenge for a chosen origin. This lets the passkey
/// grant's crypto-success path — including native-origin acceptance
/// (<c>https://&lt;rp-id&gt;</c>) — be exercised end-to-end without a real device,
/// closing ADR-0010 Gate-to-Accepted item #2.
/// </summary>
public sealed class SoftwareWebAuthnAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The credential id the authenticator returns (random, 32 bytes).</summary>
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>The user handle bound to this credential (must equal the seeded
    /// <c>StoredPasskeyCredential.UserHandle</c> so the owner callback matches).</summary>
    public byte[] UserHandle { get; }

    public SoftwareWebAuthnAuthenticator(byte[] userHandle) => UserHandle = userHandle;

    /// <summary>The public key as a COSE_Key (kty=EC2, alg=ES256, crv=P-256, x, y)
    /// — the exact form <c>StoredPasskeyCredential.PublicKey</c> /
    /// <c>MakeAssertionParams.StoredPublicKey</c> expect.</summary>
    public byte[] CosePublicKey()
    {
        var p = _key.ExportParameters(false);
        var w = new CborWriter();
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);    // kty: EC2
        w.WriteInt32(3); w.WriteInt32(-7);   // alg: ES256
        w.WriteInt32(-1); w.WriteInt32(1);   // crv: P-256
        w.WriteInt32(-2); w.WriteByteString(p.Q.X!);  // x
        w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);  // y
        w.WriteEndMap();
        return w.Encode();
    }

    /// <summary>
    /// Builds an <c>AuthenticatorAssertionRawResponse</c> JSON for the given
    /// challenge (base64url, exactly as it appears in the begin options),
    /// relying-party id, and origin. UP+UV flags are set (the begin requires
    /// UserVerification), and the signature is a valid ES256 signature over
    /// <c>authenticatorData ‖ SHA256(clientDataJSON)</c>.
    /// </summary>
    public string CreateAssertionJson(string challengeB64Url, string rpId, string origin, uint signCount = 1)
    {
        // clientDataJSON — built once and used for BOTH the signature and the wire,
        // so the server hashes exactly the bytes that were signed.
        var clientData = $"{{\"type\":\"webauthn.get\",\"challenge\":\"{challengeB64Url}\",\"origin\":\"{origin}\",\"crossOrigin\":false}}";
        var clientDataBytes = Encoding.UTF8.GetBytes(clientData);

        // authenticatorData = rpIdHash(32) ‖ flags(1) ‖ signCount(4, big-endian).
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        const byte flags = 0x05; // UP (0x01) | UV (0x04)
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        var authData = Concat(rpIdHash, [flags], counter);

        var clientDataHash = SHA256.HashData(clientDataBytes);
        var signature = _key.SignData(
            Concat(authData, clientDataHash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var response = new
        {
            id = B64Url(CredentialId),
            rawId = B64Url(CredentialId),
            type = "public-key",
            response = new
            {
                authenticatorData = B64Url(authData),
                clientDataJSON = B64Url(clientDataBytes),
                signature = B64Url(signature),
                userHandle = B64Url(UserHandle),
            },
        };
        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Builds an <c>AuthenticatorAttestationRawResponse</c> JSON for a registration
    /// ceremony (ADR-0009 native enroll): a "none"-format attestation embedding this
    /// authenticator's COSE public key + credential id. UP+UV+AT flags are set; no
    /// attestation statement is signed (fmt="none", matching
    /// AttestationConveyancePreference.None). The resulting credential verifies under
    /// the same <paramref name="rpId"/> at login.
    /// </summary>
    public string CreateAttestationJson(string challengeB64Url, string rpId, string origin)
    {
        var clientData = $"{{\"type\":\"webauthn.create\",\"challenge\":\"{challengeB64Url}\",\"origin\":\"{origin}\",\"crossOrigin\":false}}";
        var clientDataBytes = Encoding.UTF8.GetBytes(clientData);

        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        const byte flags = 0x45; // UP (0x01) | UV (0x04) | AT (0x40 — attested credential data present)
        var counter = new byte[4]; // signCount = 0 at registration

        // attestedCredentialData = aaguid(16) ‖ credentialIdLength(2 BE) ‖ credentialId ‖ COSE public key
        var aaguid = new byte[16];
        var credIdLen = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(credIdLen, (ushort)CredentialId.Length);
        var attestedCredentialData = Concat(aaguid, credIdLen, CredentialId, CosePublicKey());

        var authData = Concat(rpIdHash, [flags], counter, attestedCredentialData);

        // attestationObject = CBOR { fmt: "none", attStmt: {}, authData }
        var w = new CborWriter();
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(authData);
        w.WriteEndMap();
        var attestationObject = w.Encode();

        var response = new
        {
            id = B64Url(CredentialId),
            rawId = B64Url(CredentialId),
            type = "public-key",
            response = new
            {
                attestationObject = B64Url(attestationObject),
                clientDataJSON = B64Url(clientDataBytes),
            },
        };
        return JsonSerializer.Serialize(response);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    public static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public void Dispose() => _key.Dispose();
}
