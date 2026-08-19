using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Management;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.ChangeFeed;
using Modgud.Infrastructure.OAuth;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end contract for Modgud's generic management API authentication:
/// the existing admin cookie and delegated/M2M bearer callers all reach the
/// same endpoint permission, while OAuth scope/audience only select the API.
/// Position reads are the first deliberately exposed resource.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ManagementApiAuthorizationTests : IntegrationTestBase
{
    public ManagementApiAuthorizationTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Service_account_with_management_token_and_live_permission_can_read_positions()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreatePositionAsync("management-sa-visible");
        var serviceAccount = await CreateServiceAccountAsync("management-reader");
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));
        await GrantPositionReadAsync(serviceAccountId);

        var clientId = $"management-sa-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id,
            [ModgudManagementApi.Scope], AccessTokenType.Reference);
        var token = await IssueClientCredentialsTokenAsync(
            clientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using var response = await SendManagementGetAsync(token);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode,
            $"management GET failed ({(int)response.StatusCode}): {body}");
        var positions = JsonSerializer.Deserialize<List<PositionPrincipalDto>>(body, JsonOptions);
        Assert.Contains(positions!, candidate => candidate.Id == position.Id);
    }

    [Fact]
    public async Task Management_token_without_live_permission_is_forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await CreatePositionAsync("management-denied");
        var serviceAccount = await CreateServiceAccountAsync("management-no-role");
        var clientId = $"management-denied-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id,
            [ModgudManagementApi.Scope]);
        var token = await IssueClientCredentialsTokenAsync(
            clientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using var response = await SendManagementGetAsync(token);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Management.PermissionDenied", body);
    }

    [Fact]
    public async Task Token_for_another_resource_cannot_call_the_management_api()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await CreatePositionAsync("management-wrong-audience");
        var serviceAccount = await CreateServiceAccountAsync("management-wrong-audience-sa");
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));
        await GrantPositionReadAsync(serviceAccountId);

        var otherScope = $"management-test-other-{Guid.NewGuid():N}";
        const string otherAudience = "urn:modgud:test:other-api";
        await CreateScopeAsync(otherScope, otherAudience);
        var clientId = $"management-other-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id, [otherScope]);
        var token = await IssueClientCredentialsTokenAsync(clientId, otherScope, otherAudience);

        using var response = await SendManagementGetAsync(token);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Management.InvalidAudience", body);
    }

    [Fact]
    public async Task Deactivated_service_account_cannot_reuse_an_issued_management_token()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await CreatePositionAsync("management-deactivate");
        var serviceAccount = await CreateServiceAccountAsync("management-deactivate-sa");
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));
        await GrantPositionReadAsync(serviceAccountId);
        var clientId = $"management-deactivate-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id,
            [ModgudManagementApi.Scope]);
        var token = await IssueClientCredentialsTokenAsync(
            clientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using (var before = await SendManagementGetAsync(token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var deactivated = await Client.PutAsJsonAsync(
            $"/api/service-account/{serviceAccount.Id}", new { IsActive = false }, JsonOptions, ct);
        Assert.True(deactivated.IsSuccessStatusCode,
            await deactivated.Content.ReadAsStringAsync(ct));

        using var after = await SendManagementGetAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Removing_management_scope_from_client_blocks_an_already_issued_token()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await CreatePositionAsync("management-client-scope-live");
        var serviceAccount = await CreateServiceAccountAsync("management-client-scope-sa");
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));
        await GrantPositionReadAsync(serviceAccountId);
        var clientId = $"management-client-scope-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id,
            [ModgudManagementApi.Scope]);
        var token = await IssueClientCredentialsTokenAsync(
            clientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using (var before = await SendManagementGetAsync(token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await RemoveManagementScopeFromClientAsync(clientId);

        using var after = await SendManagementGetAsync(token);
        var body = await after.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Contains("Management.ClientScopeRevoked", body);
    }

    [Fact]
    public async Task Delegated_user_token_uses_the_same_live_position_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreatePositionAsync("management-user-visible");
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Management", lastname: "User", acronym: "mu",
            email: "management-user@test.com", password: "TestPass1234");
        await GrantPositionReadAsync(user.Id);

        var clientId = $"management-user-{Guid.NewGuid():N}";
        var clientSecret = $"management-secret-{Guid.NewGuid():N}";
        const string redirectUri = "http://localhost/management-callback";
        await CreateDelegatedClientAsync(clientId, clientSecret, redirectUri);
        var token = await IssueDelegatedTokenAsync(
            "mu", "TestPass1234", clientId, clientSecret, redirectUri);

        using var response = await SendManagementGetAsync(token);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode,
            $"delegated management GET failed ({(int)response.StatusCode}): {body}");
        var positions = JsonSerializer.Deserialize<List<PositionPrincipalDto>>(body, JsonOptions);
        Assert.Contains(positions!, candidate => candidate.Id == position.Id);
    }

    [Fact]
    public async Task Management_bearer_does_not_expose_cookie_only_position_writes()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var serviceAccount = await CreateServiceAccountAsync("management-write-boundary");
        var clientId = $"management-write-boundary-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(clientId, serviceAccount.Id,
            [ModgudManagementApi.Scope]);
        var token = await IssueClientCredentialsTokenAsync(
            clientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using var bearerClient = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/position")
        {
            Content = JsonContent.Create(new { AccountName = "management-write-not-exposed" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await bearerClient.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task App_change_feed_requires_both_live_permission_and_client_app_assignment()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.CreateVersion7();
        var otherAppId = Guid.CreateVersion7();
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Events.StartStream<App>(appId, new AppCreatedEvent(
                appId, $"management-feed-{Guid.NewGuid():N}", "Management feed", null, [], false));
            arrange.Events.StartStream<App>(otherAppId, new AppCreatedEvent(
                otherAppId, $"management-feed-other-{Guid.NewGuid():N}", "Other app", null, [], false));
            arrange.Store(new AppChangeFeedState
            {
                Id = appId,
                Enabled = true,
                Generation = 1,
                ScopeVersion = "v1-management-test",
                LastProcessedSequence = 1,
            });
            await arrange.SaveChangesAsync(ct);
        }

        var serviceAccount = await CreateServiceAccountAsync("management-feed-reader");
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid serviceAccountId));
        await GrantAppScopeReadAsync(serviceAccountId);

        var allowedClientId = $"management-feed-allowed-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(
            allowedClientId,
            serviceAccount.Id,
            [ModgudManagementApi.Scope],
            appIds: [appId]);
        var allowedToken = await IssueClientCredentialsTokenAsync(
            allowedClientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        string snapshotCursor;
        using (var allowed = await SendManagementGetAsync(
                   allowedToken,
                   $"/api/app/{ShortGuid.Encode(appId)}/change-feed/snapshot"))
        {
            var body = await allowed.Content.ReadAsStringAsync(ct);
            Assert.True(allowed.IsSuccessStatusCode,
                $"assigned feed GET failed ({(int)allowed.StatusCode}): {body}");
            using var snapshot = JsonDocument.Parse(body);
            snapshotCursor = snapshot.RootElement.GetProperty("Cursor").GetString()!;
        }

        var feedEntityId = Guid.CreateVersion7();
        await using (var append = GetTenantedDocumentSession())
        {
            var state = await append.LoadAsync<AppChangeFeedState>(appId, ct);
            state!.LastProcessedSequence = 2;
            append.Store(state);
            append.Store(new AppChangeFeedEntry
            {
                Id = Guid.CreateVersion7(),
                AppId = appId,
                Generation = 1,
                SourceSequence = 2,
                Ordinal = 0,
                ScopeVersion = state.ScopeVersion,
                OriginatedAt = DateTimeOffset.UtcNow,
                RecordedAt = DateTimeOffset.UtcNow,
                ChangeKind = "Upsert",
                EntityKind = "principal",
                EntityId = feedEntityId,
                PayloadJson = "{\"DisplayName\":\"SSE principal\"}",
            });
            await append.SaveChangesAsync(ct);
        }

        using (var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            streamCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var streamClient = Factory.CreateClient();
            using var streamRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/app/{ShortGuid.Encode(appId)}/change-feed/stream");
            streamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", allowedToken);
            streamRequest.Headers.Accept.ParseAdd("text/event-stream");
            streamRequest.Headers.TryAddWithoutValidation("Last-Event-ID", snapshotCursor);
            using var streamResponse = await streamClient.SendAsync(
                streamRequest, HttpCompletionOption.ResponseHeadersRead, streamCts.Token);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);

            await using var body = await streamResponse.Content.ReadAsStreamAsync(streamCts.Token);
            using var reader = new StreamReader(body);
            var idLine = await reader.ReadLineAsync(streamCts.Token);
            var eventLine = await reader.ReadLineAsync(streamCts.Token);
            var dataLine = await reader.ReadLineAsync(streamCts.Token);
            Assert.StartsWith("id: ", idLine);
            Assert.Equal("event: change", eventLine);
            Assert.StartsWith("data: ", dataLine);
            using var message = JsonDocument.Parse(dataLine!["data: ".Length..]);
            Assert.Equal("Change", message.RootElement.GetProperty("Kind").GetString());
            Assert.Equal(ShortGuid.Encode(feedEntityId),
                message.RootElement.GetProperty("EntityId").GetString());
        }

        var deniedClientId = $"management-feed-denied-{Guid.NewGuid():N}";
        await CreateClientCredentialsClientAsync(
            deniedClientId,
            serviceAccount.Id,
            [ModgudManagementApi.Scope],
            appIds: [otherAppId]);
        var deniedToken = await IssueClientCredentialsTokenAsync(
            deniedClientId, ModgudManagementApi.Scope, ModgudManagementApi.Audience);

        using var denied = await SendManagementGetAsync(
            deniedToken,
            $"/api/app/{ShortGuid.Encode(appId)}/change-feed/snapshot");
        var deniedBody = await denied.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Management.ClientAppMismatch", deniedBody);
    }

    [Fact]
    public async Task Realm_seeder_reconciles_a_legacy_management_scope_collision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var session = GetTenantedDocumentSession())
        {
            var current = await session.Query<OAuthScopeState>()
                .SingleAsync(scope => scope.Name == ModgudManagementApi.Scope, ct);
            var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(
                current.Id, token: ct);
            Assert.NotNull(aggregate);

            session.Events.Append(current.Id,
                aggregate!.SetDisplayName("Legacy collision"),
                aggregate.SetDescription("Unsafe pre-upgrade scope"),
                aggregate.SetResources(["urn:legacy:wrong-audience"]),
                aggregate.SetEnabled(false),
                aggregate.SetRequired(true),
                aggregate.SetEmphasize(true),
                aggregate.SetShowInDiscoveryDocument(false),
                aggregate.SetUserClaims(["email"]),
                aggregate.SetAppId(Guid.NewGuid()),
                aggregate.SetProperties(new Dictionary<string, object?>
                {
                    [ScopePropertyKeys.AllowDynamicRegistrationClients] =
                        JsonSerializer.SerializeToElement(true),
                }));
            await session.SaveChangesAsync(ct);
        }

        await OAuthRealmSeeder.SeedAsync(Factory.Services, "system", ct: ct);

        await using var query = GetTenantedSession();
        var reconciled = await query.Query<OAuthScopeState>()
            .SingleAsync(scope => scope.Name == ModgudManagementApi.Scope, ct);
        Assert.Equal("Modgud Management", reconciled.DisplayName);
        Assert.Equal([ModgudManagementApi.Audience], reconciled.Resources);
        Assert.True(reconciled.Enabled);
        Assert.False(reconciled.Required);
        Assert.False(reconciled.Emphasize);
        Assert.True(reconciled.ShowInDiscoveryDocument);
        Assert.Empty(reconciled.UserClaims);
        Assert.Null(reconciled.AppId);
        Assert.DoesNotContain(
            ScopePropertyKeys.AllowDynamicRegistrationClients,
            reconciled.Properties.Keys);
    }

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    private async Task<PositionPrincipalDto> CreatePositionAsync(string accountName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await Client.PostAsJsonAsync(
            "/api/position", new { AccountName = accountName }, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode,
            $"position arrange failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<PositionPrincipalDto>(body, JsonOptions)!;
    }

    private async Task<ServiceAccountDto> CreateServiceAccountAsync(string accountName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await Client.PostAsJsonAsync(
            "/api/service-account", new { AccountName = accountName }, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode,
            $"service-account arrange failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<ServiceAccountDto>(body, JsonOptions)!;
    }

    private async Task GrantPositionReadAsync(Guid principalId)
    {
        var role = await Factory.CreateTestRoleAsync(
            $"ManagementPositionReader_{Guid.NewGuid():N}", [("position", "read")]);
        await Factory.CreateTestGroupAsync(
            $"ManagementPositionReaders_{Guid.NewGuid():N}", [principalId], [role.Id]);
    }

    private async Task GrantAppScopeReadAsync(Guid principalId)
    {
        var role = await Factory.CreateTestRoleAsync(
            $"ManagementAppScopeReader_{Guid.NewGuid():N}", [("app-scope", "read")]);
        await Factory.CreateTestGroupAsync(
            $"ManagementAppScopeReaders_{Guid.NewGuid():N}", [principalId], [role.Id]);
    }

    private async Task CreateClientCredentialsClientAsync(
        string clientId,
        string serviceAccountId,
        List<string> scopes,
        AccessTokenType accessTokenType = AccessTokenType.Jwt,
        List<Guid>? appIds = null)
    {
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
            Scopes = scopes,
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = accessTokenType,
            AppIds = appIds?.Select(ShortGuid.Encode).ToList() ?? [],
            LinkedServiceAccountId = serviceAccountId,
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(error => $"{error.Code}: {error.Description}"))}");
    }

    private async Task CreateDelegatedClientAsync(
        string clientId,
        string clientSecret,
        string redirectUri)
    {
        using var scope = Factory.Services.CreateScope();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauth.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [redirectUri],
            PostLogoutRedirectUris = [],
            Scopes = ["openid", ModgudManagementApi.Scope],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [],
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(error => $"{error.Code}: {error.Description}"))}");
    }

    private async Task RemoveManagementScopeFromClientAsync(string clientId)
    {
        await using var session = GetTenantedDocumentSession();
        var state = await session.Query<OAuthApplicationState>()
            .SingleAsync(client => client.ClientId == clientId, TestContext.Current.CancellationToken);
        var aggregate = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(
            state.Id, token: TestContext.Current.CancellationToken);
        Assert.NotNull(aggregate);
        var scopePermission =
            OpenIddictConstants.Permissions.Prefixes.Scope + ModgudManagementApi.Scope;
        session.Events.Append(state.Id, aggregate!.SetPermissions(
            state.Permissions.Where(permission => permission != scopePermission).ToList()));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> IssueClientCredentialsTokenAsync(
        string clientId,
        string scope,
        string audience)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", $"{clientId}-secret"),
            new("scope", scope),
            new("resource", audience),
        };
        using var tokenClient = Factory.CreateClient();
        using var response = await tokenClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"client_credentials failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<string> IssueDelegatedTokenAsync(
        string username,
        string password,
        string clientId,
        string clientSecret,
        string redirectUri)
    {
        using var cookieClient = await CreateAuthenticatedClientAsync(username, password);
        var verifier = GeneratePkceVerifier();
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeUri = "/connect/authorize?" + string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString($"openid {ModgudManagementApi.Scope}")}",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(ModgudManagementApi.Audience)}",
        });
        using var authorize = await cookieClient.GetAsync(
            authorizeUri, TestContext.Current.CancellationToken);
        Assert.True((int)authorize.StatusCode is 301 or 302 or 303 or 307 or 308,
            $"authorize did not redirect ({(int)authorize.StatusCode}): " +
            await authorize.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var location = authorize.Headers.Location
            ?? throw new InvalidOperationException("Authorize response has no Location header.");
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = query["code"]
            ?? throw new InvalidOperationException($"Authorize response has no code: {location}");

        var tokenForm = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
            new("resource", ModgudManagementApi.Audience),
        };
        using var tokenClient = Factory.CreateClient();
        using var response = await tokenClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(tokenForm), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"authorization_code failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task CreateScopeAsync(string name, string audience)
    {
        using var scope = Factory.Services.CreateScope();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var result = await oauth.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            Resources = [audience],
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateScopeAsync failed: {string.Join(", ", result.Errors.Select(error => $"{error.Code}: {error.Description}"))}");
    }

    private async Task<HttpResponseMessage> SendManagementGetAsync(
        string token,
        string path = "/api/position")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = Factory.CreateClient();
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
