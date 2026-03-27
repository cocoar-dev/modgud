using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Groups;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Projections;

/// <summary>
/// Tests for GroupListProjection async projection.
/// Verifies that the denormalized group list maintains correct counts.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Projections")]
public class GroupListProjectionTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public GroupListProjectionTests(SharedPostgresFixture fixture)
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

	[Fact]
	public async Task GroupList_ContainsCreatedGroup()
	{
		await LoginAsAdminAsync();
		await _factory.CreateTestGroupAsync("ListTestGroup", "A test group");

		var response = await _client.GetAsync("/system/api/admin/groups");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<GroupListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Contains(result.Items, g => g.Name == "ListTestGroup");
	}

	[Fact]
	public async Task GroupList_ExcludesArchivedGroups()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("ToArchiveGroup");
		var shortGuid = new ShortGuid(groupId);

		// Archive the group
		await _client.DeleteAsync($"/system/api/admin/groups/{shortGuid}");

		// Check list
		var response = await _client.GetAsync("/system/api/admin/groups");
		var result = await response.ReadFromJsonAsync<GroupListDto>(_factory.JsonOptions);

		Assert.NotNull(result);
		Assert.DoesNotContain(result.Items, g => g.Name == "ToArchiveGroup");
	}

	[Fact]
	public async Task GroupList_TracksMemberCount()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("MemberCountGroup");
		var user1 = await _factory.CreateTestUserAsync("mc_user1");
		var user2 = await _factory.CreateTestUserAsync("mc_user2");
		var shortGuid = new ShortGuid(groupId);

		// Add two members
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{shortGuid}/members",
			new { userId = new ShortGuid(user1.Id).ToString() },
			_factory.JsonOptions);
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{shortGuid}/members",
			new { userId = new ShortGuid(user2.Id).ToString() },
			_factory.JsonOptions);

		// Check via repository
		using var scope = _factory.Services.CreateScope();
		var repo = scope.ServiceProvider.GetRequiredService<IGroupListRepository>();
		var groups = await repo.GetAllAsync();

		var group = groups.FirstOrDefault(g => g.Name == "MemberCountGroup");
		Assert.NotNull(group);
		Assert.Equal(2, group.MemberCount);
	}

	[Fact]
	public async Task GroupList_TracksChildGroupCount()
	{
		await LoginAsAdminAsync();
		var parentId = await _factory.CreateTestGroupAsync("ChildCountParent");
		var child1Id = await _factory.CreateTestGroupAsync("ChildCount1");
		var child2Id = await _factory.CreateTestGroupAsync("ChildCount2");

		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(parentId)}/children",
			new { childGroupId = new ShortGuid(child1Id).ToString() },
			_factory.JsonOptions);
		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(parentId)}/children",
			new { childGroupId = new ShortGuid(child2Id).ToString() },
			_factory.JsonOptions);

		using var scope = _factory.Services.CreateScope();
		var repo = scope.ServiceProvider.GetRequiredService<IGroupListRepository>();
		var groups = await repo.GetAllAsync();

		var parent = groups.FirstOrDefault(g => g.Name == "ChildCountParent");
		Assert.NotNull(parent);
		Assert.Equal(2, parent.ChildGroupCount);
	}

	[Fact]
	public async Task GroupList_TracksRoleGrantCount()
	{
		await LoginAsAdminAsync();
		var groupId = await _factory.CreateTestGroupAsync("RoleCountGroup");
		var role = await _factory.CreateTestRoleAsync("RoleCountRole");

		await _client.PostAsJsonAsync(
			$"/system/api/admin/groups/{new ShortGuid(groupId)}/roles",
			new { roleId = new ShortGuid(role.Id).ToString() },
			_factory.JsonOptions);

		using var scope = _factory.Services.CreateScope();
		var repo = scope.ServiceProvider.GetRequiredService<IGroupListRepository>();
		var groups = await repo.GetAllAsync();

		var group = groups.FirstOrDefault(g => g.Name == "RoleCountGroup");
		Assert.NotNull(group);
		Assert.Equal(1, group.RoleGrantCount);
	}
}
