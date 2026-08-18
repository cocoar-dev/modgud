using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Management;

namespace Modgud.Api.Tests.Authorization;

[Collection(IntegrationTestCollection.Name)]
public class ApplicationScopeApiTests : IntegrationTestBase
{
    public ApplicationScopeApiTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record ScopeRoot(string Id, string Name, bool HasPermissions);
    private sealed record ScopePrincipal(string Id, string Type, string DisplayName, bool IsScopeRoot);
    private sealed record ScopeResponse(
        string AppId,
        string AppSlug,
        string ScopeVersion,
        List<ScopeRoot> RootGroups,
        List<ScopePrincipal> Principals);

    [Fact]
    public async Task Full_read_uses_bound_groups_and_keeps_version_stable_for_membership_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        var serviceAccount = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            AccountName = "scope-sync",
            Purpose = "Scope API test",
        };
        var nestedId = Guid.NewGuid();
        var rootId = Guid.NewGuid();

        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(appId, new AppCreatedEvent(
                appId, "scope-test", "Scope test", null, [], IsSystem: false));
            session.Events.StartStream<PositionPrincipal>(positionId, new PositionPrincipalCreatedEvent(
                positionId, "gate", "Gate", true, PositionTerminalPolicy.Disabled));
            session.Store(serviceAccount);
            session.Events.StartStream<Group>(nestedId, new GroupCreatedEvent(
                nestedId, "Nested", null,
                [positionId, serviceAccount.Id], [], BoundTo: []));
            session.Events.StartStream<Group>(rootId, new GroupCreatedEvent(
                rootId, "Scope only", null,
                [DefaultUser!.Id, nestedId], [], BoundTo: ["scope-test"]));
            await session.SaveChangesAsync(ct);
        }

        var path = $"/api/app/{new ShortGuid(appId)}/scope";
        var firstResponse = await Client.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<ScopeResponse>(JsonOptions, ct);

        Assert.NotNull(first);
        Assert.Equal("scope-test", first.AppSlug);
        Assert.Collection(first.RootGroups, root =>
        {
            Assert.Equal(new ShortGuid(rootId).ToString(), root.Id);
            Assert.False(root.HasPermissions);
        });
        Assert.Equal(
            new[] { "group", "group", "person", "position", "service-account" },
            first.Principals.Select(p => p.Type).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(first.Principals, p => p.IsScopeRoot);

        var secondServiceAccount = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            AccountName = "scope-sync-2",
        };
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(secondServiceAccount);
            session.Events.Append(rootId, new GroupUpdatedEvent(
                rootId, "Scope only", null,
                [DefaultUser!.Id, nestedId, secondServiceAccount.Id], [],
                BoundTo: ["scope-test"]));
            await session.SaveChangesAsync(ct);
        }

        var second = await Client.GetFromJsonAsync<ScopeResponse>(path, JsonOptions, ct);
        Assert.NotNull(second);
        Assert.Equal(first.ScopeVersion, second.ScopeVersion);
        Assert.Contains(second.Principals, p => p.Id == new ShortGuid(secondServiceAccount.Id).ToString());

        var secondRootId = Guid.NewGuid();
        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<Group>(secondRootId, new GroupCreatedEvent(
                secondRootId, "Second root", null, [], [], BoundTo: ["scope-test"]));
            await session.SaveChangesAsync(ct);
        }

        var third = await Client.GetFromJsonAsync<ScopeResponse>(path, JsonOptions, ct);
        Assert.NotNull(third);
        Assert.NotEqual(second.ScopeVersion, third.ScopeVersion);
    }

    [Fact]
    public async Task Bearer_client_can_read_only_its_assigned_application_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownAppId = Guid.NewGuid();
        var foreignAppId = Guid.NewGuid();
        var serviceAccount = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            AccountName = $"scope-reader-{Guid.NewGuid():N}",
        };

        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(ownAppId, new AppCreatedEvent(
                ownAppId, $"scope-own-{Guid.NewGuid():N}", "Own App", null, [], IsSystem: false));
            session.Events.StartStream<App>(foreignAppId, new AppCreatedEvent(
                foreignAppId, $"scope-foreign-{Guid.NewGuid():N}", "Foreign App", null, [], IsSystem: false));
            session.Store(serviceAccount);
            await session.SaveChangesAsync(ct);
        }

        var role = await Factory.CreateTestRoleAsync(
            $"AppScopeReader_{Guid.NewGuid():N}", [("app-scope", "read")]);
        await Factory.CreateTestGroupAsync(
            $"AppScopeReaders_{Guid.NewGuid():N}", [serviceAccount.Id], [role.Id]);

        var clientId = $"app-scope-reader-{Guid.NewGuid():N}";
        await CreateManagementClientAsync(clientId, serviceAccount.Id, ownAppId);
        var token = await IssueManagementTokenAsync(clientId);

        using var own = await SendScopeGetAsync(token, ownAppId);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        using var foreign = await SendScopeGetAsync(token, foreignAppId);
        var body = await foreign.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains("Management.ClientAppMismatch", body);
    }

    private async Task CreateManagementClientAsync(
        string clientId,
        Guid serviceAccountId,
        Guid appId)
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
            Scopes = [ModgudManagementApi.Scope],
            AllowedGrantTypes = ["client_credentials"],
            RequireConsent = false,
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = [new ShortGuid(appId).ToString()],
            LinkedServiceAccountId = new ShortGuid(serviceAccountId).ToString(),
        }, TestContext.Current.CancellationToken);
        if (result.IsError)
        {
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(error => $"{error.Code}: {error.Description}"))}");
        }
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
            "/connect/token",
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            $"client_credentials failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> SendScopeGetAsync(string token, Guid appId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/app/{new ShortGuid(appId)}/scope");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = Factory.CreateClient();
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
