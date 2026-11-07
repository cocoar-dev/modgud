using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modgud.Domain.OAuth.Common;
using OpenIddict.Server;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-11 — the token-type filter of <c>RealmSigningKeyHandler</c>: the tokens that leave
/// the IdP (access and id tokens) are signed with the realm's key from its JWKS, never
/// with the server's global certificate.
/// </summary>
public partial class CocoarNativeGrantFlowTests
{
    [Fact]
    public async Task Access_and_id_tokens_are_signed_with_a_key_of_the_realm_jwks()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnableNativeGrantsAsync();
        await SeedNativeClientAsync("native-kid-app");
        var code = await IssueNativeOtpViaServiceAsync(DefaultUser!.Id);

        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = CocoarGrantTypes.Otp,
            ["client_id"] = "native-kid-app",
            ["client_secret"] = "native-kid-app-secret",
            ["username"] = TestEmail,
            ["otp_code"] = code,
            ["scope"] = "openid",
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(response.IsSuccessStatusCode, body.ToString());

        var realmKids = new JsonWebKeySet(await Factory.CreateClient().GetStringAsync("/.well-known/jwks", ct))
            .Keys.Select(k => k.Kid).ToHashSet();
        var globalKids = Factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>()
            .CurrentValue.SigningCredentials.Select(c => c.Key.KeyId).ToHashSet();

        foreach (var name in new[] { "access_token", "id_token" })
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty(name).GetString());
            Assert.Contains(jwt.Header.Kid, realmKids);
            Assert.DoesNotContain(jwt.Header.Kid, globalKids);
        }
    }
}
