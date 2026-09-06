using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 1 — proves the Application-subdomain map round-trips through
/// the global store and that <see cref="IRealmCache.ResolveAsync"/> resolves an
/// app host to (tenant, Application) while a plain tenant host resolves to the
/// tenant with no Application. The pure precedence/fallback rules are pinned in
/// the unit tests; this verifies the global-store wiring + cache load.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmCacheApplicationDomainTests : IntegrationTestBase
{
    public RealmCacheApplicationDomainTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ApplicationDomain_RoundTrips_And_Resolves_Tenant_And_Application()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.NewGuid();
        const string slug = "acmelist-rc";
        const string tenantHost = "acmelist-rc.localhost";
        const string appHost = "app.acmelist-rc.localhost";

        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var existing = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
            if (existing is null)
            {
                session.Store(new Realm
                {
                    Id = Guid.NewGuid(),
                    Slug = slug,
                    DisplayName = "AcmeList RC",
                    Domains = [tenantHost],
                    PrimaryDomain = tenantHost,
                    ApplicationDomains = new Dictionary<string, Guid> { [appHost] = appId },
                    IsControlPlane = false,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await session.SaveChangesAsync(ct);
            }
        }

        var cache = Factory.Services.GetRequiredService<IRealmCache>();
        cache.Invalidate();

        var appResolution = await cache.ResolveAsync(appHost);
        Assert.NotNull(appResolution);
        Assert.Equal(slug, appResolution!.Tenant.Slug);
        Assert.Equal(appId, appResolution.ApplicationId);

        var tenantResolution = await cache.ResolveAsync(tenantHost);
        Assert.NotNull(tenantResolution);
        Assert.Equal(slug, tenantResolution!.Tenant.Slug);
        Assert.Null(tenantResolution.ApplicationId);
    }
}
