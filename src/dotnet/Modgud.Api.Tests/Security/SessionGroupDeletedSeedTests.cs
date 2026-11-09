using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// D.roles-F01 — a federated session carries session group ids. When such a group was
/// soft-deleted while a parent group still listed it as member (the reference clean-up runs
/// asynchronously), the ancestor walk enqueued the deleted seed anyway and reached the parent's
/// roles through it. A deleted session group confers nothing, not even its parents.
/// </summary>
public sealed class SessionGroupDeletedSeedTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_deleted_session_group_is_no_bridge_to_its_parents_roles()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Sess", lastname: "Seed", acronym: $"ss{Guid.NewGuid():N}"[..10], email: $"ss{Guid.NewGuid():N}"[..14] + "@test.com");
        var role = await Factory.CreateTestRoleAsync($"Seed parent {Guid.NewGuid():N}", [("audit-log", "read")]);
        var child = await Factory.CreateTestGroupAsync($"Session child {Guid.NewGuid():N}", [], []);
        await Factory.CreateTestGroupAsync($"Seed parent {Guid.NewGuid():N}", [child.Id], [role.Id]);

        // The child is soft-deleted; the parent still lists it (clean-up not yet run).
        await using (var write = GetTenantedDocumentSession())
        {
            var doc = (await write.LoadAsync<Group>(child.Id, ct))!;
            doc.IsDeleted = true;
            write.Store(doc);
            await write.SaveChangesAsync(ct);
        }

        using var scope = Factory.Services.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        var granted = await permissions.GetUserPermissionsAsync(user.Id, AppSlugs.Modgud, [child.Id], ct);
        Assert.DoesNotContain("audit-log:read", granted);
    }
}
