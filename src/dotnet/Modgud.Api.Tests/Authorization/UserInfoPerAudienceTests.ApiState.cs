using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B6-01 — a disabled API stayed an audience of newly issued tokens and kept its
/// resource_access block, although the admin UI promises that only active APIs can be
/// the target of new tokens. B6-02 — the client-credentials flow built resource_access
/// for every audience its scopes grant, not for the resource= the client asked for, so a
/// token for API A carried the service account's roles and permissions for API B.
/// </summary>
public partial class UserInfoPerAudienceTests
{
    [Fact]
    public async Task Client_credentials_resource_access_covers_only_the_requested_audience()
    {
        var (clientId, alphaAudience, betaAudience) = await ArrangeTwoApiServiceAccountClientAsync("b602");

        using var response = await PostClientCredentialsAsync(
            clientId, "alpha-b602 beta-b602 roles permissions", alphaAudience);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"client_credentials failed ({(int)response.StatusCode}): {body}");

        var payload = DecodeJwtPayload(ReadAccessToken(body));
        var resourceAccess = payload.GetProperty("resource_access");
        Assert.True(resourceAccess.TryGetProperty(alphaAudience, out _),
            "the requested audience must keep its block");
        Assert.False(resourceAccess.TryGetProperty(betaAudience, out _),
            "a block for an audience the client did not request must not be in the token");
    }

    [Fact]
    public async Task A_disabled_api_is_no_audience_of_new_tokens()
    {
        var (clientId, alphaAudience, betaAudience) = await ArrangeTwoApiServiceAccountClientAsync("b601");
        await DisableApiAsync(betaAudience);

        // Without resource=: the token's audiences and blocks come from the scopes.
        using (var response = await PostClientCredentialsAsync(
                   clientId, "alpha-b601 beta-b601 roles permissions", resource: null))
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"client_credentials failed ({(int)response.StatusCode}): {body}");
            var payload = DecodeJwtPayload(ReadAccessToken(body));
            Assert.DoesNotContain(betaAudience, ReadAudiences(payload));
            Assert.Contains(alphaAudience, ReadAudiences(payload));
            var resourceAccess = payload.GetProperty("resource_access");
            Assert.False(resourceAccess.TryGetProperty(betaAudience, out _),
                "a disabled API must not get a resource_access block");
        }

        // Asking for the disabled API explicitly is refused.
        using (var response = await PostClientCredentialsAsync(
                   clientId, "alpha-b601 beta-b601 roles permissions", betaAudience))
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"a token for a disabled API must be refused, got {(int)response.StatusCode}: {body}");
        }
    }

    private async Task<(string ClientId, string AlphaAudience, string BetaAudience)> ArrangeTwoApiServiceAccountClientAsync(
        string tag)
    {
        var ct = TestContext.Current.CancellationToken;
        var appAlpha = await CreateAppAsync($"app-alpha-{tag}", "App Alpha", permissions: [("policy", "read")]);
        var appBeta = await CreateAppAsync($"app-beta-{tag}", "App Beta", permissions: [("ledger", "read")]);
        var alphaAudience = $"https://alpha-{tag}.example.com";
        var betaAudience = $"https://beta-{tag}.example.com";
        await CreateOAuthApiAsync(alphaAudience, appAlpha.Id);
        await CreateOAuthApiAsync(betaAudience, appBeta.Id);
        await CreateScopeAsync($"alpha-{tag}", [alphaAudience], appAlpha.Id);
        await CreateScopeAsync($"beta-{tag}", [betaAudience], appBeta.Id);

        using var created = await Client.PostAsJsonAsync(
            "/api/service-account", new { AccountName = $"sa-{tag}-{Guid.NewGuid():N}"[..20] }, JsonOptions, ct);
        var createdBody = await created.Content.ReadAsStringAsync(ct);
        Assert.True(created.IsSuccessStatusCode, $"service-account arrange failed ({(int)created.StatusCode}): {createdBody}");
        var serviceAccount = JsonSerializer.Deserialize<ServiceAccountDto>(createdBody, JsonOptions)!;
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));

        await GrantAsync(serviceAccountId, roleAppSlug: $"app-alpha-{tag}", resourceType: "policy",
            actions: ["read"], groupBoundTo: [$"app-alpha-{tag}"]);
        await GrantAsync(serviceAccountId, roleAppSlug: $"app-beta-{tag}", resourceType: "ledger",
            actions: ["read"], groupBoundTo: [$"app-beta-{tag}"]);

        var clientId = $"cc-{tag}-{Guid.NewGuid():N}";
        using var scope = Factory.Services.CreateScope();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauth.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"{clientId}-secret",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Scopes = [$"alpha-{tag}", $"beta-{tag}", "roles", "permissions"],
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [ShortGuid.Encode(appAlpha.Id), ShortGuid.Encode(appBeta.Id)],
            LinkedServiceAccountId = serviceAccount.Id,
        }, ct);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");

        return (clientId, alphaAudience, betaAudience);
    }

    private async Task<HttpResponseMessage> PostClientCredentialsAsync(string clientId, string scope, string? resource)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", $"{clientId}-secret"),
            new("scope", scope),
        };
        if (resource is not null) form.Add(new("resource", resource));
        using var tokenClient = Factory.CreateClient();
        return await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);
    }

    private async Task DisableApiAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var api = await session.Query<OAuthApiState>().SingleAsync(a => a.Name == name, ct);
        var aggregate = await session.Events.AggregateStreamAsync<OAuthApiAggregate>(api.Id, token: ct)
            ?? throw new InvalidOperationException($"API stream {api.Id} not found");
        session.Events.Append(api.Id, aggregate.Disable());
        await session.SaveChangesAsync(ct);
        Assert.False((await session.LoadAsync<OAuthApiState>(api.Id, ct))!.Enabled);
    }

    private static string ReadAccessToken(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private static List<string> ReadAudiences(JsonElement payload) =>
        payload.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array
                ? aud.EnumerateArray().Select(a => a.GetString()!).ToList()
                : [aud.GetString()!]
            : [];
}
