using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.Admin)]
public class GroupsAdminTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public GroupsAdminTests(SharedPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		var connectionString = await _fixture.CreateIsolatedDatabasesAsync();
		_factory = new CocoarAuthWebApplicationFactory(connectionString);
		_client = _factory.CreateClientWithCookies();
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task LoginAsAdminAsync()
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
	}

	// ── Auth Guard ──

	[Fact]
	public async Task GetGroups_WithoutAuthentication_ReturnsUnauthorized()
	{
		var response = await _client.GetAsync("/system/api/admin/groups");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	// ── CRUD ──

	[Fact]
	public async Task GetGroups_AsAdmin_ReturnsGroupList()
	{
		await LoginAsAdminAsync();
		await _factory.CreateTestGroupAsync("Group1");
		await _factory.CreateTestGroupAsync("Group2");

		var response = await _client.GetAsync("/system/api/admin/groups");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<GroupListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.TotalCount >= 2);
	}

	[Fact]
	public async Task GetGroup_WithValidId_ReturnsGroupDetail()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("DetailGroup", "Test description");
		var shortGuid = new ShortGuid(groupId);

		var response = await _client.GetAsync($"/system/api/admin/groups/{shortGuid}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("DetailGroup", result.Name);
		Assert.Equal("Test description", result.Description);
		Assert.NotNull(result.MemberIds);
		Assert.NotNull(result.ChildGroupIds);
		Assert.NotNull(result.RealmRoleGrants);
		Assert.NotNull(result.ClientRoleGrants);
	}

	[Fact]
	public async Task GetGroup_WithInvalidId_ReturnsNotFound()
	{
		await LoginAsAdminAsync();
		var nonExistentId = new ShortGuid(Guid.NewGuid());

		var response = await _client.GetAsync($"/system/api/admin/groups/{nonExistentId}");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task CreateGroup_WithValidData_ReturnsCreatedGroup()
	{
		await LoginAsAdminAsync();
		var dto = new CreateGroupDto { Name = "NewGroup", Description = "A new group" };

		var response = await _client.PostAsJsonAsync("/system/api/admin/groups", dto, _factory.JsonOptions);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("NewGroup", result.Name);
		Assert.Equal("A new group", result.Description);
	}

	[Fact]
	public async Task UpdateGroup_WithValidData_ReturnsOk()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("UpdateMe", "Old description");
		var shortGuid = new ShortGuid(groupId);

		var updateDto = new { Name = "UpdatedGroup", Description = "New description" };

		var response = await _client.PatchAsJsonAsync($"/system/api/admin/groups/{shortGuid}", updateDto, _factory.JsonOptions);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("UpdatedGroup", result.Name);
		Assert.Equal("New description", result.Description);
	}

	[Fact]
	public async Task ArchiveGroup_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("ArchiveMe");
		var shortGuid = new ShortGuid(groupId);

		var response = await _client.DeleteAsync($"/system/api/admin/groups/{shortGuid}");

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify archived group is not found via GetById
		var getResponse = await _client.GetAsync($"/system/api/admin/groups/{shortGuid}");
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
	}

	// ── Membership ──

	[Fact]
	public async Task AddMember_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("MemberGroup");
		var user = await _factory.CreateTestUserAsync("member1", "Test123!@#");
		var shortGuid = new ShortGuid(groupId);

		var response = await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{shortGuid}/members",
			new { userId = new ShortGuid(user.Id).ToString() },
			_factory.JsonOptions);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify member is in the group detail
		var getResponse = await _client.GetAsync($"/system/api/admin/groups/{shortGuid}");
		var group = await getResponse.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.Contains(group!.MemberIds, id => id == new ShortGuid(user.Id));
	}

	[Fact]
	public async Task AddMember_Duplicate_ReturnsConflict()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("DuplicateMemberGroup");
		var user = await _factory.CreateTestUserAsync("dupmember", "Test123!@#");
		var shortGuid = new ShortGuid(groupId);
		var body = new { userId = new ShortGuid(user.Id).ToString() };

		// Add once
		await _client.PostAsJsonAsync($"/system/api/admin/groups/{shortGuid}/members", body, _factory.JsonOptions);

		// Add again
		var response = await _client.PostAsJsonAsync($"/system/api/admin/groups/{shortGuid}/members", body, _factory.JsonOptions);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task RemoveMember_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("RemoveMemberGroup");
		var user = await _factory.CreateTestUserAsync("removeme", "Test123!@#");
		var shortGuid = new ShortGuid(groupId);

		// Add first
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{shortGuid}/members",
			new { userId = new ShortGuid(user.Id).ToString() },
			_factory.JsonOptions);

		// Remove
		var response = await _client.DeleteAsync(
			$"/system/api/admin/groups/{shortGuid}/members/{new ShortGuid(user.Id)}");

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	// ── Nesting ──

	[Fact]
	public async Task AddChildGroup_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var parentId = await _factory.CreateTestGroupAsync("ParentGroup");
		var childId = await _factory.CreateTestGroupAsync("ChildGroup");

		var response = await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(parentId)}/children",
			new { childGroupId = new ShortGuid(childId).ToString() },
			_factory.JsonOptions);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify child is in the group detail
		var getResponse = await _client.GetAsync($"/system/api/admin/groups/{new ShortGuid(parentId)}");
		var group = await getResponse.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.Contains(group!.ChildGroupIds, id => id == new ShortGuid(childId));
	}

	[Fact]
	public async Task AddChildGroup_SelfReference_ReturnsBadRequest()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("SelfRefGroup");
		var shortGuid = new ShortGuid(groupId);

		var response = await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{shortGuid}/children",
			new { childGroupId = shortGuid.ToString() },
			_factory.JsonOptions);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task AddChildGroup_CycleDetected_ReturnsBadRequest()
	{
		await LoginAsAdminAsync();
		var groupA = await _factory.CreateTestGroupAsync("CycleA");
		var groupB = await _factory.CreateTestGroupAsync("CycleB");

		// A -> B
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupA)}/children",
			new { childGroupId = new ShortGuid(groupB).ToString() },
			_factory.JsonOptions);

		// B -> A (should be rejected)
		var response = await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupB)}/children",
			new { childGroupId = new ShortGuid(groupA).ToString() },
			_factory.JsonOptions);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task RemoveChildGroup_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var parentId = await _factory.CreateTestGroupAsync("RemoveChildParent");
		var childId = await _factory.CreateTestGroupAsync("RemoveChildChild");

		// Add
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(parentId)}/children",
			new { childGroupId = new ShortGuid(childId).ToString() },
			_factory.JsonOptions);

		// Remove
		var response = await _client.DeleteAsync(
			$"/system/api/admin/groups/{new ShortGuid(parentId)}/children/{new ShortGuid(childId)}");

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	// ── Role Grants ──

	[Fact]
	public async Task GrantRealmRole_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("RoleGrantGroup");
		var role = await _factory.CreateTestRoleAsync("GrantedRole");

		var response = await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupId)}/roles",
			new { roleId = new ShortGuid(role.Id).ToString() },
			_factory.JsonOptions);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify grant is in group detail
		var getResponse = await _client.GetAsync($"/system/api/admin/groups/{new ShortGuid(groupId)}");
		var group = await getResponse.ReadFromJsonAsync<GroupDetailDto>(_factory.JsonOptions);
		Assert.Single(group!.RealmRoleGrants);
	}

	[Fact]
	public async Task RevokeRole_ReturnsNoContent()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("RevokeRoleGroup");
		var role = await _factory.CreateTestRoleAsync("RevokeMe");

		// Grant first
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupId)}/roles",
			new { roleId = new ShortGuid(role.Id).ToString() },
			_factory.JsonOptions);

		// Revoke
		var response = await _client.DeleteAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupId)}/roles/{new ShortGuid(role.Id)}");

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}
}
