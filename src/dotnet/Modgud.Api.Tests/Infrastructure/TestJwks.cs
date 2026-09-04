using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>RSA key pairs for <c>private_key_jwt</c> tests: the public half as a JWKS
/// document (what an admin registers), the private half as signing credentials
/// (what the client signs its assertion with).</summary>
public sealed class TestJwks : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public TestJwks(string keyId)
    {
        KeyId = keyId;
        var key = new RsaSecurityKey(_rsa) { KeyId = keyId };
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        PublicJwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = keyId,
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
        PrivateJwk = JsonSerializer.Serialize(new
        {
            kty = "RSA",
            kid = keyId,
            n = Base64UrlEncoder.Encode(parameters.Modulus),
            e = Base64UrlEncoder.Encode(parameters.Exponent),
            d = Base64UrlEncoder.Encode(_rsa.ExportParameters(true).D),
        });
    }

    public string KeyId { get; }
    public string PublicJwks { get; }
    /// <summary>A JWK that carries the private exponent — must be refused at registration.</summary>
    public string PrivateJwk { get; }
    public SigningCredentials SigningCredentials { get; }

    public void Dispose() => _rsa.Dispose();
}
