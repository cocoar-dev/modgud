using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Authentication.RateLimiting;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-AJ-02 — the caller context accepted a client assertion whose audience merely STARTED with
/// this request's origin: https://idp.example.com.evil/… or any path under the origin passed. The
/// audience must name this server: its issuer, its token endpoint or the endpoint being called.
/// </summary>
public class ClientAssertionAudienceTests
{
    private const string ClientId = "aud-client";

    [Theory]
    [InlineData("https://idp.example.com", true)]
    [InlineData("https://idp.example.com/", true)]
    [InlineData("https://idp.example.com/connect/token", true)]
    [InlineData("https://idp.example.com.evil/connect/token", false)]
    [InlineData("https://idp.example.com/anything/else", false)]
    public async Task The_audience_must_name_this_server(string audience, bool accepted)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "k1" };
        var jwks = new JsonWebKeySet();
        jwks.Keys.Add(JsonWebKeyConverter.ConvertFromRSASecurityKey(key));

        var now = DateTime.UtcNow;
        var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ClientId,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", ClientId)]),
            IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("idp.example.com");
        http.Request.Path = "/connect/token";

        Assert.Equal(accepted, await ClientAssertionValidator.IsValidAsync(assertion, ClientId, jwks, http.Request));
    }
}
