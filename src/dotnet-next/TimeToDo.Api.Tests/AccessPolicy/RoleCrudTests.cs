using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;

namespace TimeToDo.Api.Tests.AccessPolicy;

/// <summary>
/// Integration tests for Role CRUD endpoints via the full HTTP stack.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RoleCrudTests : IntegrationTestBase
{
    public RoleCrudTests(SharedPostgresFixture fixture) : base(fixture) { }

    private record RoleResponse(string Id, string Name, string? Description, string ResourceType, List<string> Permissions);
    private record LookupItem(string Id, string Name, string ResourceType);

    // ── Create ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Role_ReturnsCreatedRole()
    {
        var dto = new { Name = "Todo Viewer", Description = "Read-only todos", ResourceType = "todo", Permissions = new List<string> { "read" } };

        var response = await Client.PostAsJsonAsync("/api/role", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<RoleResponse>(JsonOptions);
        Assert.NotNull(result.Id);
        Assert.Equal("Todo Viewer", result.Name);
        Assert.Equal("todo", result.ResourceType);
        Assert.Equal(["read"], result.Permissions);
    }

    // ── Get All ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Roles_ReturnsAllRoles()
    {
        await Factory.CreateTestRoleAsync("Role A", "todo", ["read"]);
        await Factory.CreateTestRoleAsync("Role B", "customer", ["read", "update"]);

        var response = await Client.GetAsync("/api/role", TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<List<RoleResponse>>(JsonOptions);
        Assert.Contains(result, r => r.Name == "Role A");
        Assert.Contains(result, r => r.Name == "Role B");
    }

    // ── Get By Id ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingRole_ReturnsRole()
    {
        var role = await Factory.CreateTestRoleAsync("My Role", "todo", ["read", "update"]);
        var id = new ShortGuid(role.Id).ToString();

        var response = await Client.GetAsync($"/api/role/{id}", TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<RoleResponse>(JsonOptions);
        Assert.Equal(id, result.Id);
        Assert.Equal("My Role", result.Name);
        Assert.Contains("read", result.Permissions);
        Assert.Contains("update", result.Permissions);
    }

    [Fact]
    public async Task GetById_NonExistentRole_ReturnsNotFound()
    {
        var response = await Client.GetAsync($"/api/role/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Update ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Role_ReturnsUpdatedRole()
    {
        var role = await Factory.CreateTestRoleAsync("Old Name", "todo", ["read"]);
        var id = new ShortGuid(role.Id).ToString();

        var dto = new { Name = "New Name", Description = "Updated desc", ResourceType = "todo", Permissions = new List<string> { "read", "update", "delete" } };

        var response = await Client.PutAsJsonAsync($"/api/role/{id}", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<RoleResponse>(JsonOptions);
        Assert.Equal("New Name", result.Name);
        Assert.Equal("Updated desc", result.Description);
        Assert.Equal(3, result.Permissions.Count);
        Assert.Contains("delete", result.Permissions);
    }

    [Fact]
    public async Task Update_NonExistentRole_ReturnsNotFound()
    {
        var dto = new { Name = "Ghost", Description = (string?)null, ResourceType = "todo", Permissions = new List<string>() };

        var response = await Client.PutAsJsonAsync($"/api/role/{new ShortGuid(Guid.NewGuid())}", dto, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Role_RemovesRole()
    {
        var role = await Factory.CreateTestRoleAsync("To Delete", "todo", ["read"]);
        var id = new ShortGuid(role.Id).ToString();

        var response = await Client.DeleteAsync($"/api/role/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await Client.GetAsync($"/api/role/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistentRole_ReturnsNotFound()
    {
        var response = await Client.DeleteAsync($"/api/role/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Lookup ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_ReturnsIdNameAndResourceType()
    {
        await Factory.CreateTestRoleAsync("Viewer", "todo", ["read"]);

        var response = await Client.GetAsync("/api/role/lookup", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.ReadFromJsonAsync<List<LookupItem>>(JsonOptions);
        Assert.NotNull(result);
        var viewer = result?.FirstOrDefault(r => r.Name == "Viewer");
        Assert.NotNull(viewer);
        Assert.Equal("todo", viewer.ResourceType);
    }
}
