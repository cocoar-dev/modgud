using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Issue #130 — <c>UpdateServiceAccountCredentialAsync</c> wrote an
/// <c>AccessTokenLifetime</c> edit into the display-only <c>modgud:*</c>
/// Settings key but never touched OpenIddict's native <c>tkn_lft:act</c>
/// key, so editing an EXISTING SA credential's lifetime had zero effect on
/// subsequently minted tokens — only the create path
/// (<c>IssueServiceAccountCredentialAsync</c>, via <c>CreateClientAsync</c>)
/// wired <c>tkn_lft:act</c>. This proves the update path now routes through
/// the same <c>OAuthAdminMapping.ApplyNativeTokenLifetimes</c> validate-
/// then-write helper as create — mirrors <see cref="StandardClientTokenLifetimeFlowTests"/>
/// for the standard-client (issue #115) case.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ServiceAccountCredentialLifetimeUpdateTests : IntegrationTestBase
{
    public ServiceAccountCredentialLifetimeUpdateTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Updating_AccessTokenLifetime_On_An_Existing_Credential_Changes_The_Effective_Token_Lifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        var serviceAccountId = await CreateServiceAccountAsync("sa-credential-lifetime-update");

        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var issueDto = new IssueServiceAccountCredentialDto
        {
            ClientId = "sa-cred-lifetime-update",
            Scopes = [],
            AppIds = [],
            // Reference tokens are opaque server-side handles — JWT is
            // required so the test can read exp/iat straight off the token.
            AccessTokenType = AccessTokenType.Jwt,
            AccessTokenLifetime = 900, // 15 min — the value the update below overwrites
        };
        var issued = await oauthAdmin.IssueServiceAccountCredentialAsync(serviceAccountId, issueDto, ct);
        Assert.False(issued.IsError);
        var clientId = issued.Value.Credential.ClientId;
        var credentialId = issued.Value.Credential.Id;
        var clientSecret = issued.Value.ClientSecret;

        var initialLifetimeMinutes = await MintAndGetLifetimeMinutesAsync(clientId, clientSecret);
        Assert.InRange(initialLifetimeMinutes, 14, 16);

        var updateDto = new UpdateServiceAccountCredentialDto { AccessTokenLifetime = 1800 }; // 30 min
        var updated = await oauthAdmin.UpdateServiceAccountCredentialAsync(serviceAccountId, credentialId, updateDto, ct);
        Assert.False(updated.IsError);

        var updatedLifetimeMinutes = await MintAndGetLifetimeMinutesAsync(clientId, clientSecret);
        Assert.InRange(updatedLifetimeMinutes, 29, 31);
    }

    [Fact]
    public async Task Updating_AccessTokenLifetime_Below_The_1_Minute_Floor_Is_Rejected_Not_Silently_Ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var serviceAccountId = await CreateServiceAccountAsync("sa-credential-lifetime-reject");

        using var scope = Factory.Services.CreateScope();
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var issueDto = new IssueServiceAccountCredentialDto
        {
            ClientId = "sa-cred-lifetime-reject",
            Scopes = [],
            AppIds = [],
            AccessTokenType = AccessTokenType.Jwt,
        };
        var issued = await oauthAdmin.IssueServiceAccountCredentialAsync(serviceAccountId, issueDto, ct);
        Assert.False(issued.IsError);

        var updateDto = new UpdateServiceAccountCredentialDto { AccessTokenLifetime = 30 }; // below the 60s floor
        var updated = await oauthAdmin.UpdateServiceAccountCredentialAsync(
            serviceAccountId, issued.Value.Credential.Id, updateDto, ct);

        Assert.True(updated.IsError);
        Assert.Contains(updated.Errors, e => e.Code == "OAuthClient.InvalidAccessTokenLifetime");
    }

    // ─────────────────────────────── Helpers ──────────────────────────────────

    private async Task<Guid> CreateServiceAccountAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "issue-130-sa-credential-update" }, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var idRaw = dto.GetProperty("Id").GetString()!;
        Assert.True(ShortGuid.TryParse(idRaw, out Guid id), $"Expected a parseable ShortGuid, got '{idRaw}'.");
        return id;
    }

    private async Task<double> MintAndGetLifetimeMinutesAsync(string clientId, string clientSecret)
    {
        var resp = await Factory.CreateClient().PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/token failed ({(int)resp.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString()!;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        // jwt.ValidFrom defaults to MinValue when nbf is absent — compute from
        // iat/exp claims instead (same approach as StandardClientTokenLifetimeFlowTests).
        var iat = long.Parse(jwt.Payload["iat"].ToString()!);
        var exp = long.Parse(jwt.Payload["exp"].ToString()!);
        return (exp - iat) / 60.0;
    }
}
