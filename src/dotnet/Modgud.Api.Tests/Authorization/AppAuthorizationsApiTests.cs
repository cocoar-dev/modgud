using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Management;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Audit;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// The Management-API pair a consuming application uses for its "connected
/// systems" screen: list the authorizations that reach its App, and end one —
/// refresh token included.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AppAuthorizationsApiTests : IntegrationTestBase
{
    public AppAuthorizationsApiTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record AuthorizationRow(string Id, string Subject, string? ClientId, string Type, List<string> Scopes);
    private sealed record AuthorizationPage(List<AuthorizationRow> Items, string? NextAfter);

    [Fact]
    public async Task List_returns_only_authorizations_that_reach_the_callers_app()
    {
        var rig = await SeedAsync();
        var viaApi = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [Scopes.OpenId, rig.ResourceScope]);
        var viaAppScope = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.AppScopedScope]);
        var foreign = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [Scopes.OpenId, rig.ForeignScope]);

        var page = await ListAsync(rig.Token, rig.OwnAppId);

        Assert.Contains(page.Items, x => x.Id == viaApi.AuthorizationId);
        Assert.Contains(page.Items, x => x.Id == viaAppScope.AuthorizationId);
        Assert.DoesNotContain(page.Items, x => x.Id == foreign.AuthorizationId);
        Assert.Null(page.NextAfter);

        var row = page.Items.Single(x => x.Id == viaApi.AuthorizationId);
        Assert.Equal(rig.UserId.ToString(), row.Subject);
        Assert.Equal(rig.ConnectedClientId, row.ClientId);
        Assert.Equal(AuthorizationTypes.Permanent, row.Type);
    }

    [Fact]
    public async Task List_pages_without_dropping_or_repeating_rows()
    {
        var rig = await SeedAsync();
        var expected = new HashSet<string>();
        for (var i = 0; i < 5; i++)
            expected.Add((await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.ResourceScope])).AuthorizationId);

        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = await ListAsync(rig.Token, rig.OwnAppId, after: after, limit: 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            after = page.NextAfter;
        } while (after is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(expected.IsSubsetOf(seen), "paging dropped an authorization");
    }

    [Fact]
    public async Task Revoke_ends_the_authorization_and_its_refresh_token_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await SeedAsync();
        var grant = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.ResourceScope]);

        using var first = await DeleteAsync(rig.Token, rig.OwnAppId, grant.AuthorizationId);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        await using (var session = GetTenantedDocumentSession())
        {
            var authorization = await session.LoadAsync<OpenIddictAuthorizationDocument>(grant.AuthorizationId, ct);
            var token = await session.LoadAsync<OpenIddictTokenDocument>(grant.RefreshTokenId, ct);
            Assert.Equal(Statuses.Revoked, authorization!.Status);
            Assert.Equal(Statuses.Revoked, token!.Status);
        }

        var page = await ListAsync(rig.Token, rig.OwnAppId);
        Assert.DoesNotContain(page.Items, x => x.Id == grant.AuthorizationId);

        using var second = await DeleteAsync(rig.Token, rig.OwnAppId, grant.AuthorizationId);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await using (var session = GetTenantedDocumentSession())
        {
            var audited = await session.Query<RealmSecurityAuditEvent>()
                .Where(x => x.EventType == AuditEvents.AuthorizationRevoked
                            && x.AuthorizationId == grant.AuthorizationId)
                .ToListAsync(ct);
            var entry = Assert.Single(audited); // the idempotent repeat is not a second revoke
            Assert.Equal(rig.UserId, entry.TargetSubjectId);
            Assert.Equal(rig.ServiceAccountId, entry.ActorSubjectId);
        }
    }

    [Fact]
    public async Task Revoke_answers_404_for_an_authorization_outside_the_app_and_leaves_it_valid()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await SeedAsync();
        var foreign = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.ForeignScope]);

        using var response = await DeleteAsync(rig.Token, rig.OwnAppId, foreign.AuthorizationId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var session = GetTenantedDocumentSession();
        var authorization = await session.LoadAsync<OpenIddictAuthorizationDocument>(foreign.AuthorizationId, ct);
        var token = await session.LoadAsync<OpenIddictTokenDocument>(foreign.RefreshTokenId, ct);
        Assert.Equal(Statuses.Valid, authorization!.Status);
        Assert.Equal(Statuses.Valid, token!.Status);
    }

    [Fact]
    public async Task Caller_cannot_target_an_app_its_client_is_not_assigned_to()
    {
        var rig = await SeedAsync();
        var foreign = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.ForeignScope]);

        using var list = await SendAsync(HttpMethod.Get, rig.Token,
            $"/api/app/{new ShortGuid(rig.ForeignAppId)}/authorizations");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        using var revoke = await DeleteAsync(rig.Token, rig.ForeignAppId, foreign.AuthorizationId);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    [Fact]
    public async Task Read_permission_alone_does_not_allow_revoking()
    {
        var rig = await SeedAsync(permissions: [("oauth-authorization", "read")]);
        var grant = await CreateAuthorizationAsync(rig.ConnectedClientPk, rig.UserId, [rig.ResourceScope]);

        using var response = await DeleteAsync(rig.Token, rig.OwnAppId, grant.AuthorizationId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Management.PermissionDenied", body);
    }

    // ─── Rig ─────────────────────────────────────────────────────────────

    private sealed record Rig(
        Guid OwnAppId, Guid ForeignAppId, Guid UserId, Guid ServiceAccountId, string Token,
        string ConnectedClientId, string ConnectedClientPk,
        string ResourceScope, string AppScopedScope, string ForeignScope);

    private sealed record Grant(string AuthorizationId, string RefreshTokenId);

    private async Task<Rig> SeedAsync((string Resource, string Action)[]? permissions = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ownAppId = Guid.NewGuid();
        var foreignAppId = Guid.NewGuid();
        var serviceAccount = new ServiceAccount { Id = Guid.NewGuid(), AccountName = $"authz-sa-{tag}" };

        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(ownAppId, new AppCreatedEvent(
                ownAppId, $"authz-own-{tag}", "Own App", null, [], IsSystem: false));
            session.Events.StartStream<App>(foreignAppId, new AppCreatedEvent(
                foreignAppId, $"authz-foreign-{tag}", "Foreign App", null, [], IsSystem: false));
            session.Store(serviceAccount);
            await session.SaveChangesAsync(ct);
        }

        var role = await Factory.CreateTestRoleAsync(
            $"AuthzManager_{tag}",
            permissions ?? [("oauth-authorization", "read"), ("oauth-authorization", "revoke")]);
        await Factory.CreateTestGroupAsync($"AuthzManagers_{tag}", [serviceAccount.Id], [role.Id]);

        var apiName = $"https://authz-own-{tag}.test/";
        var resourceScope = $"own-{tag}.read";      // global scope, reaches the App via its API
        var appScopedScope = $"own-{tag}.app";      // app-scoped, no resource at all
        var foreignScope = $"foreign-{tag}.read";

        using (var scope = NewSystemTenantScope())
        {
            var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
            AssertOk(await oauth.CreateApiAsync(new CreateOAuthApiDto
            {
                Name = apiName, DisplayName = apiName, AppId = new ShortGuid(ownAppId).ToString(),
            }, ct));
            AssertOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Name = resourceScope, DisplayName = resourceScope, Resources = [apiName],
            }, ct));
            AssertOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Name = appScopedScope, DisplayName = appScopedScope, AppId = new ShortGuid(ownAppId).ToString(),
            }, ct));
            AssertOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Name = foreignScope, DisplayName = foreignScope, AppId = new ShortGuid(foreignAppId).ToString(),
            }, ct));

            // The "connected system" whose grants are listed and revoked.
            var connectedClientId = $"authz-connected-{tag}";
            AssertOk(await oauth.CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = connectedClientId,
                ClientType = OAuthClientTypes.Public,
                ConsentType = OAuthConsentTypes.Explicit,
                DisplayName = connectedClientId,
                RedirectUris = ["http://localhost/cb"],
                PostLogoutRedirectUris = [],
                Scopes = [Scopes.OpenId, resourceScope, appScopedScope, foreignScope],
                AllowedGrantTypes = ["authorization_code", "refresh_token"],
                AccessTokenType = AccessTokenType.Jwt,
            }, ct));

            var managementClientId = $"authz-mgmt-{tag}";
            AssertOk(await oauth.CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = managementClientId,
                ClientSecret = $"{managementClientId}-secret",
                ClientType = OAuthClientTypes.Confidential,
                ConsentType = OAuthConsentTypes.Implicit,
                DisplayName = managementClientId,
                RedirectUris = [],
                PostLogoutRedirectUris = [],
                Scopes = [ModgudManagementApi.Scope],
                AllowedGrantTypes = ["client_credentials"],
                RequireConsent = false,
                AccessTokenType = AccessTokenType.Jwt,
                AppIds = [new ShortGuid(ownAppId).ToString()],
                LinkedServiceAccountId = new ShortGuid(serviceAccount.Id).ToString(),
            }, ct));

            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var connected = await applications.FindByClientIdAsync(connectedClientId, ct);
            var connectedPk = await applications.GetIdAsync(connected!, ct);

            return new Rig(
                ownAppId, foreignAppId, UserId: Guid.NewGuid(), serviceAccount.Id,
                await IssueManagementTokenAsync(managementClientId),
                connectedClientId, connectedPk!,
                resourceScope, appScopedScope, foreignScope);
        }
    }

    /// <summary>A Permanent authorization plus the refresh token hanging off
    /// it — the state an Authorization Code flow with offline_access leaves.</summary>
    private async Task<Grant> CreateAuthorizationAsync(string clientPk, Guid userId, string[] scopes)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = NewSystemTenantScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        var identity = new ClaimsIdentity("test");
        identity.SetClaim(Claims.Subject, userId.ToString());
        var authorization = await authorizations.CreateAsync(
            new ClaimsPrincipal(identity), userId.ToString(), clientPk,
            AuthorizationTypes.Permanent, scopes.ToImmutableArray(), ct);
        var authorizationId = (await authorizations.GetIdAsync(authorization, ct))!;

        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            ApplicationId = clientPk,
            AuthorizationId = authorizationId,
            Subject = userId.ToString(),
            Type = TokenTypeHints.RefreshToken,
            Status = Statuses.Valid,
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(14),
        }, ct);

        return new Grant(authorizationId, (await tokens.GetIdAsync(token, ct))!);
    }

    private async Task<string> IssueManagementTokenAsync(string clientId)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", $"{clientId}-secret"),
            new("scope", ModgudManagementApi.Scope),
            new("resource", ModgudManagementApi.Audience),
        };
        using var tokenClient = Factory.CreateClient();
        using var response = await tokenClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"client_credentials failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<AuthorizationPage> ListAsync(string token, Guid appId, string? after = null, int? limit = null)
    {
        var query = new List<string>();
        if (after is not null) query.Add($"after={Uri.EscapeDataString(after)}");
        if (limit is not null) query.Add($"limit={limit}");
        var url = $"/api/app/{new ShortGuid(appId)}/authorizations"
                  + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var response = await SendAsync(HttpMethod.Get, token, url);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"list failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<AuthorizationPage>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private Task<HttpResponseMessage> DeleteAsync(string token, Guid appId, string authorizationId) =>
        SendAsync(HttpMethod.Delete, token,
            $"/api/app/{new ShortGuid(appId)}/authorizations/{Uri.EscapeDataString(authorizationId)}");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string token, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = Factory.CreateClient();
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }

    private static void AssertOk<T>(ErrorOr.ErrorOr<T> result) =>
        Assert.False(result.IsError,
            string.Join(", ", result.ErrorsOrEmptyList.Select(e => $"{e.Code}: {e.Description}")));
}
