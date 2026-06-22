using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 2 — end-to-end proof that the first-signal-consistency check is
/// wired into <c>/connect/token</c>: a client_credentials request on App-Y's
/// subdomain presenting a client bound to App-X is rejected with our cross-app
/// error, while the same client on App-X's subdomain (or a realm-wide client
/// anywhere) is not rejected by this gate. client_credentials is used because it
/// passes straight through to the token handler (no code/refresh token for
/// OpenIddict to pre-validate), so the gate — which runs first in ExchangeAsync —
/// is what fires. The pure decision matrix is pinned in
/// <c>FirstSignalConsistencyTests</c>.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FirstSignalConsistencyFlowTests : IntegrationTestBase
{
    private const string CrossAppError = "not associated with the application for this origin";

    public FirstSignalConsistencyFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CrossApp_Client_On_Foreign_App_Subdomain_Is_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var appX = await CreateAppAsync("fsc-appx");
        var appY = await CreateAppAsync("fsc-appy");
        var sa = await CreateServiceAccountAsync("fsc-sa-x");
        await CreateClientCredentialsClientAsync("fsc-bound-x", sa, [new ShortGuid(appX.Id).ToString()]);
        await MapApplicationDomainsAsync(("fsc-appy.localhost", appY.Id), ("fsc-appx.localhost", appX.Id));

        // App-Y host + client bound to App-X → cross-app violation. The OAuth
        // error renders as a 4xx with our description in the body, and fires
        // before any grant processing.
        var rejected = await PostTokenAsync("fsc-appy.localhost", "fsc-bound-x");
        var rejectedBody = await rejected.Content.ReadAsStringAsync(ct);
        Assert.True(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"expected a 4xx, got {(int)rejected.StatusCode}: {rejectedBody}");
        Assert.Contains(CrossAppError, rejectedBody);

        // Same client on its OWN App-X host → the consistency gate does not fire.
        var consistent = await PostTokenAsync("fsc-appx.localhost", "fsc-bound-x");
        var consistentBody = await consistent.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(CrossAppError, consistentBody);
    }

    [Fact]
    public async Task RealmWide_Client_Passes_The_Gate_On_Any_App_Subdomain()
    {
        var ct = TestContext.Current.CancellationToken;
        var appY = await CreateAppAsync("fsc-rw-appy");
        var sa = await CreateServiceAccountAsync("fsc-sa-rw");
        await CreateClientCredentialsClientAsync("fsc-realmwide", sa, appIds: []); // realm-wide
        await MapApplicationDomainsAsync(("fsc-rw-appy.localhost", appY.Id));

        var resp = await PostTokenAsync("fsc-rw-appy.localhost", "fsc-realmwide");
        var body = await resp.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain(CrossAppError, body);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostTokenAsync(string host, string clientId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = $"{clientId}-secret",
            }),
        };
        req.Headers.Host = host;
        return Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task<string> CreateServiceAccountAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "adr-0011-phase2" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return dto.GetProperty("Id").GetString()!;
    }

    private async Task CreateClientCredentialsClientAsync(string clientId, string serviceAccountId, List<string> appIds)
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
            AppIds = appIds,
            LinkedServiceAccountId = serviceAccountId,
        };
        var result = await oauthAdmin.CreateClientAsync(dto, TestContext.Current.CancellationToken);
        if (result.IsError)
            throw new InvalidOperationException(
                $"CreateClientAsync failed: {string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
    }

    /// <summary>Adds host→App.Id entries to the system realm's ApplicationDomains
    /// (global store) and invalidates the realm cache so the middleware resolves
    /// the app subdomains on the next request.</summary>
    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>()
                .FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
