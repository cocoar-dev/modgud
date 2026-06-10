using System.Net;
using Marten;
using Modgud.Api.Tests.Infrastructure;
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
    public async Task Rebuild_Endpoint_Succeeds_And_Preserves_UserView()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Reb", "Endpoint", "rbe", "rbe@test.com", "TestPass1234", isRealmAdmin: false);

        var resp = await Client.PostAsync("/api/admin/projections/rebuild", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The UserView is rebuilt from events and the per-tenant daemon resumed.
        await using var session = GetTenantedSession();
        var view = await session.LoadAsync<UserView>(user.Id, ct);
        Assert.NotNull(view);
        Assert.Equal("rbe@test.com", view!.Email);
    }
}
