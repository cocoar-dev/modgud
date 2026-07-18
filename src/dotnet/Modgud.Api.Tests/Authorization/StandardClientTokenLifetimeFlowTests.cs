using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Issue #115 — a standard (admin-created) OAuth client's Identity/Access/
/// Sliding-Refresh token-lifetime fields were persisted into display-only
/// <c>modgud:*</c> Settings keys but never enforced, because no OpenIddict
/// handler read them (only DCR/CIMD/native-grants clients got the
/// OpenIddict-native <c>tkn_lft:*</c> keys). <see cref="OAuthAdminService"/>
/// now ALSO writes those native keys for standard clients (see
/// <c>OAuthAdminMapping.ApplyNativeTokenLifetimes</c>, unit-pinned for the
/// pure validate+build logic in <c>OAuthAdminMappingTests</c>). This proves
/// the end-to-end wiring: a client_credentials client created with a custom
/// <c>AccessTokenLifetime</c> gets an issued access token whose exp-iat
/// matches, and an out-of-range value is rejected at create time instead of
/// being silently ignored.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class StandardClientTokenLifetimeFlowTests : IntegrationTestBase
{
    public StandardClientTokenLifetimeFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Custom_AccessTokenLifetime_Is_Honored_By_The_Issued_Token()
    {
        var serviceAccountId = await CreateServiceAccountAsync("std-lifetime-sa");
        const string clientId = "std-lifetime-client";
        const int accessTokenLifetimeSeconds = 900; // 15 min — distinct from every realm default (1h)

        await CreateClientCredentialsClientAsync(clientId, serviceAccountId, accessTokenLifetimeSeconds);

        var accessToken = await MintClientCredentialsTokenAsync(clientId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        // jwt.ValidFrom defaults to MinValue when nbf is absent, which makes
        // .ValidTo - .ValidFrom useless — compute from iat/exp claims instead
        // (same approach as DcrFullFlowTests / CocoarNativeGrantFlowTests).
        var iat = long.Parse(jwt.Payload["iat"].ToString()!);
        var exp = long.Parse(jwt.Payload["exp"].ToString()!);
        var lifetimeMinutes = (exp - iat) / 60.0;

        // ±60s wall-clock-skew allowance, matching the DCR/native-grant lifetime tests.
        Assert.InRange(lifetimeMinutes, 14, 16);
    }

    [Fact]
    public async Task AccessTokenLifetime_Below_The_1_Minute_Floor_Is_Rejected_Not_Silently_Ignored()
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = "std-lifetime-rejected",
            ClientType = OAuthClientTypes.Public,
            AllowedGrantTypes = ["authorization_code"],
            AccessTokenLifetime = 30, // below the 60-second floor
        };

        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "OAuthClient.InvalidAccessTokenLifetime");
    }

    [Fact]
    public async Task AccessTokenLifetime_Above_The_60_Minute_Ceiling_Is_Rejected_Not_Silently_Ignored()
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = "std-lifetime-rejected-high",
            ClientType = OAuthClientTypes.Public,
            AllowedGrantTypes = ["authorization_code"],
            AccessTokenLifetime = 3700, // above the 3600-second ceiling
        };

        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "OAuthClient.InvalidAccessTokenLifetime");
    }

    // ─────────────────────────────── Helpers ──────────────────────────────────

    private async Task<string> CreateServiceAccountAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "issue-115-lifetime-flow" }, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dto.GetProperty("Id").GetString()!;
    }

    private async Task CreateClientCredentialsClientAsync(
        string clientId, string serviceAccountId, int accessTokenLifetimeSeconds)
    {
        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var dto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = [],
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [], // realm-wide → passes the first-signal-consistency gate on any host
            LinkedServiceAccountId = serviceAccountId,
            AccessTokenLifetime = accessTokenLifetimeSeconds,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    private async Task<string> MintClientCredentialsTokenAsync(string clientId)
    {
        var resp = await Factory.CreateClient().PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = $"{clientId}-secret",
            }),
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }
}
