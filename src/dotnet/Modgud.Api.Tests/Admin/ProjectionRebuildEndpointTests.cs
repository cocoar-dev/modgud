using System.Net;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.Admin;

/// <summary>
/// Exercises the admin projection-rebuild ENDPOINT (audit M8). The
/// ProjectionRebuildTests cover the rebuild mechanics by driving a daemon
/// directly; this one drives the HTTP endpoint so the per-tenant daemon pause
/// (<c>DaemonForDatabase(tenantId).StopAllAsync()</c>) and per-tenant side-effect
/// suppression are exercised end-to-end against the caller's realm.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ProjectionRebuildEndpointTests : IntegrationTestBase
{
    public ProjectionRebuildEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Rebuild_Endpoint_Succeeds_And_Preserves_All_Principal_Subtypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Reb", "Endpoint", "rbe", "rbe@test.com", "TestPass1234", isRealmAdmin: false);
        var authorizationGroup = await Factory.CreateTestGroupAsync(
            $"Endpoint_{Guid.NewGuid():N}"[..18], [user.Id]);
        var serviceAccount = new ServiceAccount
        {
            Id = Guid.CreateVersion7(),
            AccountName = $"endpoint-{Guid.NewGuid():N}"[..22],
            Purpose = "Projection endpoint regression",
            IsActive = true,
        };
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(serviceAccount);
            await arrange.SaveChangesAsync(ct);
        }

        var resp = await Client.PostAsync("/api/admin/projections/rebuild", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Async views are rebuilt, both event-sourced Principal subtypes coexist,
        // and the directly stored ServiceAccount survives their shared table cleanup.
        await using var session = GetTenantedSession();
        var view = await session.LoadAsync<UserView>(user.Id, ct);
        Assert.NotNull(view);
        Assert.Equal("rbe@test.com", view!.Email);
        Assert.NotNull(await session.LoadAsync<Person>(user.Id, ct));
        Assert.NotNull(await session.LoadAsync<Group>(authorizationGroup.Id, ct));
        var preservedServiceAccount = await session.LoadAsync<ServiceAccount>(serviceAccount.Id, ct);
        Assert.NotNull(preservedServiceAccount);
        Assert.Equal(serviceAccount.Purpose, preservedServiceAccount!.Purpose);
    }
}
