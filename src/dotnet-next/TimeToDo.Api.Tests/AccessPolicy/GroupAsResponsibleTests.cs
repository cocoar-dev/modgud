using System.Net;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Authentication.Domain;

namespace TimeToDo.Api.Tests.AccessPolicy;

[Collection(IntegrationTestCollection.Name)]
public class GroupAsResponsibleTests : IntegrationTestBase
{
    public GroupAsResponsibleTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task UserCanSeeTodo_WhenAssignedToGroupTheyAreMemberOf()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Alice", "Team", "AT", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "Outsider", "OO");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        var team = await Factory.CreateTestGroupAsync("MyTeam",
            memberIds: [user.Id],
            roleIds: [role.Id],
            accessScripts:
            [
                TimeTodoWebApplicationFactory.BuildAccessScript(
                    "todo",
                    "(t) => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id))")
            ]);

        // Assign a todo where the GROUP is responsible (not the user directly)
        await Factory.CreateTestTodoAsync(title: "Group-owned Todo", responsibleUserIds: [team.Id]);
        await Factory.CreateTestTodoAsync(title: "Other's Todo", responsibleUserIds: [otherUser.Id]);

        using var client = await CreateAuthenticatedClientAsync("at", "TestPass1234");
        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Single(todos);
        Assert.Equal("Group-owned Todo", todos[0].Title);
    }

    [Fact]
    public async Task UserCanSeeTodo_WhenAssignedDirectlyOrViaGroup()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Bob", "Mixed", "BM", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "X", "OX");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        var team = await Factory.CreateTestGroupAsync("BobTeam",
            memberIds: [user.Id],
            roleIds: [role.Id],
            accessScripts:
            [
                TimeTodoWebApplicationFactory.BuildAccessScript(
                    "todo",
                    "(t) => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id))")
            ]);

        var direct = await Factory.CreateTestTodoAsync(title: "Direct", responsibleUserIds: [user.Id]);
        var viaGroup = await Factory.CreateTestTodoAsync(title: "ViaGroup", responsibleUserIds: [team.Id]);
        var unrelated = await Factory.CreateTestTodoAsync(title: "Unrelated", responsibleUserIds: [otherUser.Id]);

        using var client = await CreateAuthenticatedClientAsync("bm", "TestPass1234");
        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(2, todos.Count);
        Assert.Contains(todos, t => t.Title == "Direct");
        Assert.Contains(todos, t => t.Title == "ViaGroup");
        Assert.DoesNotContain(todos, t => t.Title == "Unrelated");
    }

    [Fact]
    public async Task NestedGroup_UserInLeaf_SeesTodoAssignedToAncestor()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Carol", "Deep", "CD", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        var leaf = await Factory.CreateTestGroupAsync("LeafGroup", memberIds: [user.Id]);
        var root = await Factory.CreateTestGroupAsync("RootGroup",
            memberIds: [leaf.Id],
            roleIds: [role.Id],
            accessScripts:
            [
                TimeTodoWebApplicationFactory.BuildAccessScript(
                    "todo",
                    "(t) => t.Responsibles.some(r => r.Id === user.Id || user.GroupIds.includes(r.Id))")
            ]);

        // Todo assigned to the ROOT group — should be visible to Carol via nested membership
        await Factory.CreateTestTodoAsync(title: "RootLevel", responsibleUserIds: [root.Id]);

        using var client = await CreateAuthenticatedClientAsync("cd", "TestPass1234");
        var response = await client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Single(todos);
        Assert.Equal("RootLevel", todos[0].Title);
    }
}
