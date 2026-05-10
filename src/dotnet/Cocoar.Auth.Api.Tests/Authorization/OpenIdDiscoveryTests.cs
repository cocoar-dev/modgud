using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Api.Tests.Authorization;

/// <summary>
/// Pins the parity between the issuer value published in
/// <c>/.well-known/openid-configuration</c> and the <c>iss</c> claim
/// stamped onto issued tokens.
///
/// <para>Regression guard for the trailing-slash mismatch: discovery
/// used to publish <c>"issuer": "https://host/"</c> while the token
/// <c>iss</c> was the trimmed <c>"https://host"</c>. OpenIdConnect-handler
/// clients then rejected id_tokens with
/// <c>SecurityTokenInvalidIssuerException IDX10205</c>.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OpenIdDiscoveryTests : IntegrationTestBase
{
    public OpenIdDiscoveryTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Discovery_issuer_matches_iss_claim_in_issued_access_token()
    {
        // ── Arrange ──────────────────────────────────────────────────────
        // Pure client_credentials so we don't need to stand up a user/role/
        // group fixture; the iss claim is stamped by the same handler
        // regardless of grant type.
        const string scopeName = "discovery-iss-test";
        await CreateScopeAsync(scopeName);

        var clientSecret = "TestClientSecret_" + Guid.NewGuid().ToString("N");
        var clientId = "test-discovery-iss-" + Guid.NewGuid().ToString("N");
        await CreateOAuthClientAsync(clientId, clientSecret, [scopeName]);

        // ── Act ──────────────────────────────────────────────────────────
        var client = Factory.CreateClient();

        var discoveryResp = await client.GetAsync("/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);
        Assert.True(discoveryResp.IsSuccessStatusCode);
        var discoveryBody = await discoveryResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var discoveryDoc = JsonDocument.Parse(discoveryBody);
        var discoveryIssuer = discoveryDoc.RootElement.GetProperty("issuer").GetString();

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scopeName,
        });
        var tokenResp = await client.PostAsync("/connect/token", tokenForm,
            TestContext.Current.CancellationToken);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode,
            $"/connect/token failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        using var tokenJson = JsonDocument.Parse(tokenBody);
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var tokenIssuer = jwt.Issuer;

        // ── Assert ───────────────────────────────────────────────────────
        Assert.NotNull(discoveryIssuer);
        Assert.Equal(discoveryIssuer, tokenIssuer);
    }

    private async Task CreateScopeAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            Resources = ["https://discovery-iss-test.cocoar.local"],
        };
        var result = await oauthAdmin.CreateScopeAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateScopeAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task CreateOAuthClientAsync(string clientId, string clientSecret, List<string> scopes)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = scopes,
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }
}
