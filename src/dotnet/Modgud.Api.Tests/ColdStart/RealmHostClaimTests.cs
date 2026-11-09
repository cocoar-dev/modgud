using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// F.routing-F-R1 / RV-0002 — a host routes to exactly one realm. Realm domains were checked only
/// against other realms' plain domains, never against their Application subdomains (which win in
/// the routing lookup), and the CLI's realm-add-domain checked nothing across realms. Either way a
/// second realm could claim a host that kept routing to the first one.
/// </summary>
public class RealmHostClaimTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task A_realm_cannot_claim_another_realms_application_subdomain_or_domain()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();

        var b = await realms.CreateRealmAsync(new CreateRealmDto
        {
            Slug = "claim-b", DisplayName = "Claim B", Domains = ["claim-b.localhost"],
        }, ct);
        Assert.False(b.IsError, b.IsError ? b.FirstError.Description : null);
        await MapApplicationSubdomainAsync(factory, "claim-b", "app.claim-b.localhost", ct);

        var a = await realms.CreateRealmAsync(new CreateRealmDto
        {
            Slug = "claim-a", DisplayName = "Claim A", Domains = ["claim-a.localhost", "app.claim-b.localhost"],
        }, ct);
        Assert.True(a.IsError, "a realm claimed another realm's application subdomain");
        Assert.Equal("Realm.DomainTaken", a.FirstError.Code);

        // The operator CLI refuses the same claims.
        foreach (var taken in new[] { "app.claim-b.localhost", "claim-b.localhost" })
        {
            var cli = await CliHarness.RunAsync(host.Services, "realm-add-domain", "--slug", "system", "--domain", taken);
            Assert.True(cli.ExitCode != 0, $"the CLI added '{taken}', a host of realm claim-b: {cli.StdOut}");
        }
    }

    private static async Task MapApplicationSubdomainAsync(
        ColdStartWebApplicationFactory factory, string slug, string subdomain, CancellationToken ct)
    {
        await using var session = factory.Services.GetRequiredService<IGlobalStore>().LightweightSession();
        var realm = await session.Query<Realm>().SingleAsync(r => r.Slug == slug, ct);
        realm.ApplicationDomains[subdomain] = Guid.NewGuid();
        session.Store(realm);
        await session.SaveChangesAsync(ct);
    }
}
