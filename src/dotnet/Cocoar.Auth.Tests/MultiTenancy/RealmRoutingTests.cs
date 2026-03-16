using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.MultiTenancy;

[Collection(IntegrationTestCollection.Name)]
public class RealmRoutingTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public RealmRoutingTests(SharedPostgresFixture fixture)
	{
		_factory = new CocoarAuthWebApplicationFactory(fixture);
		_client = _factory.CreateClientWithCookies();
	}

	public Task InitializeAsync() => _factory.CleanDatabaseAsync();

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
	public async Task SystemPaths_WithoutRealmPrefix_StillWork()
	{
		// Backward compatibility: requests without /realms/ prefix go to system realm
		var response = await _client.GetAsync("/api/setup/status");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task HealthEndpoint_StillWorks()
	{
		var response = await _client.GetAsync("/health");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task RealmPath_InvalidRealm_Returns404()
	{
		var response = await _client.GetAsync("/realms/nonexistent/api/setup/status");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RealmPath_ValidRealm_RoutesToRealmEndpoint()
	{
		// Create a realm first
		await LoginAsAdminAsync();
		var dto = new CreateRealmDto { Slug = "routing-test", DisplayName = "Routing Test" };
		var createResponse = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// Access the realm's setup status via realm-scoped URL
		var response = await _client.GetAsync("/realms/routing-test/api/setup/status");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task RealmPath_DeactivatedRealm_Returns404()
	{
		await LoginAsAdminAsync();

		// Create and deactivate
		var dto = new CreateRealmDto { Slug = "deactivated", DisplayName = "Deactivated" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		await _client.DeleteAsync("/api/admin/realms/deactivated");

		// Access should fail
		var response = await _client.GetAsync("/realms/deactivated/api/setup/status");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RealmAdminEndpoints_NotAccessibleFromNonSystemRealm()
	{
		await LoginAsAdminAsync();

		// Create a realm
		var dto = new CreateRealmDto { Slug = "non-system", DisplayName = "Non System" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		// Try to access realm admin from non-system realm path
		var response = await _client.GetAsync("/realms/non-system/api/admin/realms");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RealmPath_SetupStatus_ShowsNeedsSetup()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "needs-setup", DisplayName = "Needs Setup" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		// New realm should need setup (no admin user)
		var response = await _client.GetAsync("/realms/needs-setup/api/setup/status");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var content = await response.Content.ReadAsStringAsync();
		Assert.Contains("true", content, StringComparison.OrdinalIgnoreCase);
	}
}
