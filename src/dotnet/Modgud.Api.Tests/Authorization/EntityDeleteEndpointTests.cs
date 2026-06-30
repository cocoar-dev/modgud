using System.Net;
using BuildingBlocks.Helper;
using Modgud.Api.Tests.Infrastructure;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Pins the canonical delete endpoints after they were consolidated onto shared
/// operations (the realm-provisioning prune reuses the same ops). The group delete
/// in particular now routes through <c>DeleteGroupCommand</c> on the Wolverine bus —
/// this proves the handler is actually discovered at runtime, not just compiles.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class EntityDeleteEndpointTests : IntegrationTestBase
{
    public EntityDeleteEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task DELETE_role_soft_deletes_and_is_gone()
    {
        var roleId = await SeedRoleAsync($"DeletableRole_{Guid.NewGuid():N}");

        var del = await Client.DeleteAsync(
            $"/api/role/{new ShortGuid(roleId)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await Client.GetAsync(
            $"/api/role/{new ShortGuid(roleId)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DELETE_missing_role_returns_404()
    {
        var del = await Client.DeleteAsync(
            $"/api/role/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    [Fact]
    public async Task DELETE_group_routes_through_the_bus_soft_deletes_and_is_gone()
    {
        // Confirms Wolverine discovers DeleteGroupHandler (the endpoint InvokeAsync's
        // DeleteGroupCommand) — a missing handler would throw 500 here.
        var group = await Factory.CreateTestGroupAsync(
            name: $"DeletableGroup_{Guid.NewGuid():N}", memberIds: [], roleIds: []);

        var del = await Client.DeleteAsync(
            $"/api/group/{new ShortGuid(group.Id)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await Client.GetAsync(
            $"/api/group/{new ShortGuid(group.Id)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DELETE_missing_group_returns_404()
    {
        var del = await Client.DeleteAsync(
            $"/api/group/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    private async Task<Guid> SeedRoleAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // PermissionRoleProjection (inline) writes the doc from the event — direct Store
        // conflicts under Marten 8.34+ optimistic concurrency. A realm-admin role grants
        // something without needing an App link.
        var id = Guid.NewGuid();
        session.Events.StartStream(id, new PermissionRoleCreatedEvent(
            id, name, Description: null, AppId: null, IsRealmAdmin: true, PermissionIds: []));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
