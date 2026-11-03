using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// DCR/CIMD-B5-04 — the discovery document advertised registration_endpoint and
/// client_id_metadata_document_supported from the realm switches only, while
/// registration and CIMD resolution use the effective (App ⊕ realm) setting of the
/// App whose domain the request came in on. Realm off / App on hid a working
/// mechanism; realm on / App off advertised one that is then refused.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DiscoveryAppOverrideTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Discovery_on_an_app_domain_follows_the_apps_effective_switches(bool realmEnabled, bool appEnabled)
    {
        var host = $"disc-{Guid.NewGuid():N}"[..14] + ".localhost";
        await SetRealmSwitchesAsync(realmEnabled);
        var app = await CreateAppAsync($"disc-app-{Guid.NewGuid():N}"[..16]);
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(new ApplicationSettings
            {
                Id = app.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Dcr = new ApplicationDcrOverrides { Enabled = appEnabled },
                Cimd = new ApplicationCimdOverrides { Enabled = appEnabled },
            });
            await session.SaveChangesAsync(Ct);
        }
        await MapApplicationDomainAsync(host, app.Id);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Host = host;
        using var response = await Factory.CreateClient().SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(appEnabled, doc.RootElement.TryGetProperty("registration_endpoint", out _));
        Assert.Equal(appEnabled, doc.RootElement.TryGetProperty("client_id_metadata_document_supported", out _));
    }

    private async Task SetRealmSwitchesAsync(bool enabled)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { ["TenantId"] = TenantConstants.SystemTenantId } };
        await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>().PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto { Enabled = enabled },
            Cimd = new UpdateCimdSettingsDto { Enabled = enabled },
        }, Ct);
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(Ct);
        return (await session.LoadAsync<App>(id, Ct))!;
    }

    private async Task MapApplicationDomainAsync(string host, Guid appId)
    {
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", Ct);
            Assert.NotNull(systemRealm);
            systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm);
            await session.SaveChangesAsync(Ct);
        }
        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
