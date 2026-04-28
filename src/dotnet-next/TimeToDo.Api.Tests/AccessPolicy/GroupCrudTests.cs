using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authorization.Principals;

namespace TimeToDo.Api.Tests.AccessPolicy;

/// <summary>
/// Integration tests for Group CRUD endpoints via the full HTTP + Wolverine stack.
/// These tests catch regressions like missing Wolverine IncludeAssembly registrations
/// that would only surface at runtime, not in security/auth tests.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class GroupCrudTests : IntegrationTestBase
{
    public GroupCrudTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ── Response record (mirrors GroupEndpoints.MapToResponse) ──────────

    private record GroupResponse(
        string Id,
        string Name,
        string? Description,
        List<string> MemberIds,
        List<string> RoleIds,
        List<AccessScriptResponse> AccessScripts,
        string MembershipMode,
        string? MembershipScript,
        string? MembershipLastError,
        string? Email,
        string EmailMode);

    private record AccessScriptResponse(string ResourceType, string? Script);

    // ── Create ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Group_ReturnsCreatedGroup()
    {
        var dto = new
        {
            Name = "Test Group",
            Description = "A test group",
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Manual",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PostAsJsonAsync("/api/group", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.NotNull(result.Id);
        Assert.Equal("Test Group", result.Name);
        Assert.Equal("A test group", result.Description);
        Assert.Equal("Manual", result.MembershipMode);
        Assert.Empty(result.MemberIds);
    }

    [Fact]
    public async Task Create_Group_WithMembers_ReturnsGroupWithMemberIds()
    {
        var user = await Factory.CreateTestUserAsync("Alice", "Test", "AT");
        var userId = new ShortGuid(user.Id).ToString();

        var dto = new
        {
            Name = "Group With Members",
            Description = (string?)null,
            MemberIds = new List<string> { userId },
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Manual",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PostAsJsonAsync("/api/group", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Single(result.MemberIds);
        Assert.Equal(userId, result.MemberIds[0]);
    }

    // ── Get All ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Groups_ReturnsAllGroups()
    {
        await Factory.CreateTestGroupAsync("Group A", []);
        await Factory.CreateTestGroupAsync("Group B", []);

        var response = await Client.GetAsync("/api/group", TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<List<GroupResponse>>(JsonOptions);
        Assert.Contains(result, g => g.Name == "Group A");
        Assert.Contains(result, g => g.Name == "Group B");
    }

    // ── Get By Id ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingGroup_ReturnsGroup()
    {
        var group = await Factory.CreateTestGroupAsync("My Group", []);
        var id = new ShortGuid(group.Id).ToString();

        var response = await Client.GetAsync($"/api/group/{id}", TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Equal(id, result.Id);
        Assert.Equal("My Group", result.Name);
    }

    [Fact]
    public async Task GetById_NonExistentGroup_ReturnsNotFound()
    {
        var response = await Client.GetAsync($"/api/group/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Update (goes through Wolverine — the regression this test guards) ─

    [Fact]
    public async Task Update_Group_ReturnsUpdatedGroup()
    {
        var group = await Factory.CreateTestGroupAsync("Original Name", []);
        var id = new ShortGuid(group.Id).ToString();

        var dto = new
        {
            Name = "Updated Name",
            Description = "New description",
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Manual",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PutAsJsonAsync($"/api/group/{id}", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Equal("Updated Name", result.Name);
        Assert.Equal("New description", result.Description);
    }

    [Fact]
    public async Task Update_Group_AddMember_MemberAppearsInResponse()
    {
        var user = await Factory.CreateTestUserAsync("Bob", "Test", "BT");
        var group = await Factory.CreateTestGroupAsync("Empty Group", []);
        var groupId = new ShortGuid(group.Id).ToString();
        var userId = new ShortGuid(user.Id).ToString();

        var dto = new
        {
            Name = "Empty Group",
            Description = (string?)null,
            MemberIds = new List<string> { userId },
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Manual",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PutAsJsonAsync($"/api/group/{groupId}", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Single(result.MemberIds);
        Assert.Equal(userId, result.MemberIds[0]);
    }

    [Fact]
    public async Task Update_NonExistentGroup_ReturnsNotFound()
    {
        var dto = new
        {
            Name = "Ghost",
            Description = (string?)null,
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Manual",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PutAsJsonAsync($"/api/group/{new ShortGuid(Guid.NewGuid())}", dto, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Group_RemovesGroup()
    {
        var group = await Factory.CreateTestGroupAsync("To Delete", []);
        var id = new ShortGuid(group.Id).ToString();

        var response = await Client.DeleteAsync($"/api/group/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await Client.GetAsync($"/api/group/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistentGroup_ReturnsNotFound()
    {
        var response = await Client.DeleteAsync($"/api/group/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Lookup ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_ReturnsIdAndName()
    {
        await Factory.CreateTestGroupAsync("Lookup Group", []);

        var response = await Client.GetAsync("/api/group/lookup", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.ReadFromJsonAsync<List<LookupItem>>(JsonOptions);
        Assert.NotNull(result);
        Assert.Contains(result, g => g.Name == "Lookup Group");
    }

    private record LookupItem(string Id, string Name);

    // ── Auto-Membership via Type.Is ───────────────────────────────────────

    [Fact]
    public async Task Create_AutoGroup_WithTypeIsScript_RecalculatesMembers()
    {
        var alice = await Factory.CreateTestUserAsync("Alice", "Active", "AA");

        var dto = new
        {
            Name = "All Alices",
            Description = (string?)null,
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Auto",
            MembershipScript = "(p) => Type.Is(p, 'person') && p.Firstname === 'Alice'",
            Email = (string?)null,
            EmailMode = "Shared",
        };

        var response = await Client.PostAsJsonAsync("/api/group", dto, JsonOptions, TestContext.Current.CancellationToken);

        var result = await response.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Equal("Auto", result.MembershipMode);
        Assert.Null(result.MembershipLastError);
        Assert.Single(result.MemberIds);
        Assert.Equal(new ShortGuid(alice.Id).ToString(), result.MemberIds[0]);
    }

    [Fact]
    public async Task Update_AutoGroup_ChangeScript_RecalculatesMembers()
    {
        var alice = await Factory.CreateTestUserAsync("Alice", "Test", "AT2");
        var bob = await Factory.CreateTestUserAsync("Bob", "Test", "BT2");

        // Create auto group matching Alice
        var create = new
        {
            Name = "Dynamic Group",
            Description = (string?)null,
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Auto",
            MembershipScript = "(p) => Type.Is(p, 'person') && p.Firstname === 'Alice'",
            Email = (string?)null,
            EmailMode = "Shared",
        };
        var createResponse = await Client.PostAsJsonAsync("/api/group", create, JsonOptions, TestContext.Current.CancellationToken);
        var created = await createResponse.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Single(created.MemberIds);

        // Update script to match Bob instead
        var update = new
        {
            Name = "Dynamic Group",
            Description = (string?)null,
            MemberIds = new List<string>(),
            RoleIds = new List<string>(),
            AccessScripts = new List<object>(),
            MembershipMode = "Auto",
            MembershipScript = "(p) => Type.Is(p, 'person') && p.Firstname === 'Bob'",
            Email = (string?)null,
            EmailMode = "Shared",
        };
        var updateResponse = await Client.PutAsJsonAsync($"/api/group/{created.Id}", update, JsonOptions, TestContext.Current.CancellationToken);

        var updated = await updateResponse.ReadSuccessJsonAsync<GroupResponse>(JsonOptions);
        Assert.Single(updated.MemberIds);
        Assert.Equal(new ShortGuid(bob.Id).ToString(), updated.MemberIds[0]);
        Assert.Null(updated.MembershipLastError);
    }
}
