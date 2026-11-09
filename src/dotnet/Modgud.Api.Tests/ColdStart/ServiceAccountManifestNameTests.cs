using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// SA-CO-01 — person, service account and position share one account-name namespace. The
/// manifest create path checked persons and positions but not an existing service account,
/// so a second "sync-agent" became a different principal under the same name, unlike the
/// REST create and the manifest rename, which refuse it.
/// </summary>
public class ServiceAccountManifestNameTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task A_manifest_cannot_create_a_second_service_account_under_a_taken_name()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "saname";
        var first = await ProvisionRealmAsync(factory, Shell(slug), new RealmManifest
        {
            ServiceAccounts = [new RealmManifestServiceAccount { AccountName = "sync-agent" }],
        }, ct);
        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : string.Empty);

        var second = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            ServiceAccounts = [new RealmManifestServiceAccount { AccountName = "sync-agent" }],
        }, ct: ct);

        Assert.True(second.IsError, "a second service account with a taken name was created");
        Assert.Contains(second.Errors, e => e.Code == "ServiceAccount.AccountNameTaken");

        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        Assert.Equal(1, await session.Query<ServiceAccount>()
            .CountAsync(s => !s.IsDeleted && s.AccountName == "sync-agent", ct));
    }
}
