using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Identity;

namespace TimeToDo.Api.Tests.AccessPolicy;

[Collection(IntegrationTestCollection.Name)]
public class NestedGroupsTests : IntegrationTestBase
{
    public NestedGroupsTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetUserGroups_TransitiveMembership_IncludesAncestors()
    {
        var user = await Factory.CreateTestUserAsync("Alice", "Anderson", "AA");

        var leaf = await Factory.CreateTestGroupAsync("Leaf", memberIds: [user.Id]);
        var middle = await Factory.CreateTestGroupAsync("Middle", memberIds: [leaf.Id]);
        var root = await Factory.CreateTestGroupAsync("Root", memberIds: [middle.Id]);

        using var scope = Factory.Services.CreateScope();
        var permission = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var groups = await permission.GetUserGroupsAsync(user.Id, TestContext.Current.CancellationToken);
        var groupIds = groups.Select(g => g.Id).ToHashSet();

        Assert.Contains(leaf.Id, groupIds);
        Assert.Contains(middle.Id, groupIds);
        Assert.Contains(root.Id, groupIds);
    }

    [Fact]
    public async Task GetUserGroups_DirectMembershipOnly_WhenNoNesting()
    {
        var user = await Factory.CreateTestUserAsync("Bob", "Direct", "BD");
        var group = await Factory.CreateTestGroupAsync("Solo", memberIds: [user.Id]);

        using var scope = Factory.Services.CreateScope();
        var permission = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var groups = await permission.GetUserGroupsAsync(user.Id, TestContext.Current.CancellationToken);

        Assert.Single(groups);
        Assert.Equal(group.Id, groups[0].Id);
    }

    [Fact]
    public async Task GetUserGroups_TransitivePermissions_InheritsFromAncestor()
    {
        var user = await Factory.CreateTestUserAsync("Carol", "Trans", "CT");

        var role = await Factory.CreateTestRoleAsync("TodoReader", "todo", ["read"]);
        var parent = await Factory.CreateTestGroupAsync("Parent", memberIds: [], roleIds: [role.Id]);
        var child = await Factory.CreateTestGroupAsync("Child", memberIds: [user.Id, parent.Id]);
        // Carol is direct member of Child; Parent is also a member of Child.
        // So Carol does NOT inherit Parent's role via "Parent is a sub-group of Child".
        // Instead, make Parent an ANCESTOR of Child: Parent.MemberIds contains Child.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var parentReloaded = await session.LoadAsync<Group>(parent.Id, TestContext.Current.CancellationToken);
        parentReloaded!.MemberIds = [child.Id];
        session.Store(parentReloaded);
        session.Events.Append(parent.Id, new TimeToDo.Authorization.Events.GroupUpdatedEvent(
            parent.Id, parent.Name, parent.Description,
            parentReloaded.MemberIds, parent.RoleIds, parent.AccessScripts,
            parent.MembershipMode, parent.MembershipScript, parent.CompiledMembershipScript));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var permission = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        var roles = await permission.GetUserRolesAsync(user.Id, TestContext.Current.CancellationToken);

        Assert.Contains(roles, r => r.Id == role.Id);
    }

    [Fact]
    public async Task GetDescendantGroupIds_ReturnsTransitiveChildren()
    {
        var leaf = await Factory.CreateTestGroupAsync("L", memberIds: []);
        var middle = await Factory.CreateTestGroupAsync("M", memberIds: [leaf.Id]);
        var root = await Factory.CreateTestGroupAsync("R", memberIds: [middle.Id]);

        using var scope = Factory.Services.CreateScope();
        var permission = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var descendants = await permission.GetDescendantGroupIdsAsync(root.Id, TestContext.Current.CancellationToken);

        Assert.Contains(middle.Id, descendants);
        Assert.Contains(leaf.Id, descendants);
    }

    [Fact]
    public async Task GetUserGroups_CycleIsHandledWithoutInfiniteLoop()
    {
        // Manually construct a cycle bypassing the cycle-detection validator
        // (which prevents creating cycles via the command handler).
        var user = await Factory.CreateTestUserAsync("Dave", "Cycle", "DC");
        var a = await Factory.CreateTestGroupAsync("CyclicA", memberIds: [user.Id]);
        var b = await Factory.CreateTestGroupAsync("CyclicB", memberIds: [a.Id]);

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            // Create cycle: A now contains B (which contains A)
            var aDoc = await session.LoadAsync<Group>(a.Id, TestContext.Current.CancellationToken);
            aDoc!.MemberIds = [user.Id, b.Id];
            session.Store(aDoc);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope2 = Factory.Services.CreateScope();
        var permission = scope2.ServiceProvider.GetRequiredService<IPermissionService>();

        // Should not hang — visited-set breaks the cycle
        var groups = await permission.GetUserGroupsAsync(user.Id, TestContext.Current.CancellationToken);
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        Assert.Contains(a.Id, groupIds);
        Assert.Contains(b.Id, groupIds);
    }
}
