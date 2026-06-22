using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 — first-signal-consistency on the cookie /me introspection path. On an
/// Application subdomain the App being introspected must match that App: an explicit
/// ?app= naming a different App is rejected, the App's own slug (or omitting ?app=)
/// is accepted. On a plain tenant host (no Host pin) the operator may query any App.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MeAppConsistencyTests : IntegrationTestBase
{
    public MeAppConsistencyTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Me_On_App_Subdomain_Enforces_App_Match()
    {
        var ct = TestContext.Current.CancellationToken;
        var appX = await CreateAppAsync("me-appx");
        var appY = await CreateAppAsync("me-appy");
        await MapApplicationDomainsAsync(("me-x.localhost", appX.Id));

        // Host pins App-X, explicit ?app=me-appy → cross-app probe → 403.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await GetMeAsync(host: "me-x.localhost", app: "me-appy")).StatusCode);

        // Host pins App-X, explicit ?app=me-appx (matches) → OK.
        Assert.Equal(HttpStatusCode.OK,
            (await GetMeAsync(host: "me-x.localhost", app: "me-appx")).StatusCode);

        // Host pins App-X, ?app omitted → defaults to the Host App → OK.
        Assert.Equal(HttpStatusCode.OK,
            (await GetMeAsync(host: "me-x.localhost", app: null)).StatusCode);

        // Plain tenant host (no Host pin) → any App allowed (operator choice).
        Assert.Equal(HttpStatusCode.OK,
            (await GetMeAsync(host: "localhost", app: "me-appy")).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> GetMeAsync(string host, string? app)
    {
        var url = app is null ? "/api/v1/me/permissions" : $"/api/v1/me/permissions?app={app}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
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

    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
