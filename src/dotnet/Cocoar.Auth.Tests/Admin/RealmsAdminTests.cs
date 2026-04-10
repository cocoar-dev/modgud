using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.Admin)]
public class RealmsAdminTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public RealmsAdminTests(SharedPostgresFixture fixture)
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
	public async Task GetRealms_WithoutAuthentication_ReturnsUnauthorized()
	{
		var response = await _client.GetAsync("/api/admin/realms");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetRealms_AsAdmin_ReturnsRealmList()
	{
		await LoginAsAdminAsync();

		var response = await _client.GetAsync("/api/admin/realms");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var result = await response.ReadFromJsonAsync<RealmListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.TotalCount >= 1); // At least system realm
		Assert.Contains(result.Items, r => r.Slug == "system" && r.CanManageTenants);
	}

	[Fact]
	public async Task CreateRealm_AsAdmin_SucceedsAndProvisionesDatabase()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto
		{
			Slug = "test-realm",
			DisplayName = "Test Realm",
			Description = "A test realm"
		};

		var response = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);

		var realm = await response.ReadFromJsonAsync<RealmDto>(_factory.JsonOptions);
		Assert.NotNull(realm);
		Assert.Equal("test-realm", realm.Slug);
		Assert.Equal("Test Realm", realm.DisplayName);
		Assert.Equal("A test realm", realm.Description);
		Assert.True(realm.IsActive);
		Assert.False(realm.CanManageTenants);
		Assert.True(realm.NeedsSetup);
	}

	[Fact]
	public async Task CreateRealm_DuplicateSlug_ReturnsConflict()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "dup-realm", DisplayName = "First" };
		var first = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);

		var second = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
	}

	[Fact]
	public async Task CreateRealm_InvalidSlug_ReturnsBadRequest()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "INVALID", DisplayName = "Bad" };
		var response = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task CreateRealm_ReservedSlug_ReturnsBadRequest()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "system", DisplayName = "Nope" };
		var response = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task GetRealm_BySlug_ReturnsRealm()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "get-test", DisplayName = "Get Test" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		var response = await _client.GetAsync("/api/admin/realms/get-test");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var realm = await response.ReadFromJsonAsync<RealmDto>(_factory.JsonOptions);
		Assert.NotNull(realm);
		Assert.Equal("get-test", realm.Slug);
	}

	[Fact]
	public async Task GetRealm_NonExistent_ReturnsNotFound()
	{
		await LoginAsAdminAsync();

		var response = await _client.GetAsync("/api/admin/realms/nonexistent");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task UpdateRealm_ChangesDisplayName()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "update-test", DisplayName = "Original" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		var updateDto = new UpdateRealmDto { DisplayName = "Updated" };
		var request = new HttpRequestMessage(HttpMethod.Patch, "/api/admin/realms/update-test")
		{
			Content = JsonContent.Create(updateDto, options: _factory.JsonOptions)
		};
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var realm = await response.ReadFromJsonAsync<RealmDto>(_factory.JsonOptions);
		Assert.NotNull(realm);
		Assert.Equal("Updated", realm.DisplayName);
	}

	[Fact]
	public async Task DeleteRealm_SoftDeletes()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "delete-test", DisplayName = "Delete Me" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		var response = await _client.DeleteAsync("/api/admin/realms/delete-test");
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify realm is deactivated
		var getResponse = await _client.GetAsync("/api/admin/realms/delete-test");
		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
		var realm = await getResponse.ReadFromJsonAsync<RealmDto>(_factory.JsonOptions);
		Assert.NotNull(realm);
		Assert.False(realm.IsActive);
	}

	[Fact]
	public async Task DeleteRealm_SystemRealm_ReturnsBadRequest()
	{
		await LoginAsAdminAsync();

		var response = await _client.DeleteAsync("/api/admin/realms/system");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
