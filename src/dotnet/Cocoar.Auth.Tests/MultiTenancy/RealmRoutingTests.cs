using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.MultiTenancy;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.MultiTenancy)]
public class RealmRoutingTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public RealmRoutingTests(SharedPostgresFixture fixture)
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
	public async Task SystemRealm_HostHeader_RoutesToSystemRealm()
	{
		// System realm is accessed via Host: system.localhost (set by default in CreateClientWithCookies)
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
	public async Task UnknownHost_Returns404()
	{
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
		request.Headers.Host = "nonexistent.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task ValidRealm_RoutesToRealmEndpoint()
	{
		// Create a realm first
		await LoginAsAdminAsync();
		var dto = new CreateRealmDto { Slug = "routing-test", DisplayName = "Routing Test" };
		var createResponse = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// Access the realm's setup status via Host header
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
		request.Headers.Host = "routing-test.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task DeactivatedRealm_Returns404()
	{
		await LoginAsAdminAsync();

		// Create and deactivate
		var dto = new CreateRealmDto { Slug = "deactivated", DisplayName = "Deactivated" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		await _client.DeleteAsync("/api/admin/realms/deactivated");

		// Access should fail
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
		request.Headers.Host = "deactivated.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RealmAdminEndpoints_NotAccessibleFromNonSystemRealm()
	{
		await LoginAsAdminAsync();

		// Create a realm
		var dto = new CreateRealmDto { Slug = "non-system", DisplayName = "Non System" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		// Try to access realm admin from non-system realm host
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/realms");
		request.Headers.Host = "non-system.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RealmPath_SetupStatus_ShowsNeedsSetup()
	{
		await LoginAsAdminAsync();

		var dto = new CreateRealmDto { Slug = "needs-setup", DisplayName = "Needs Setup" };
		await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);

		// New realm should need setup (no admin user)
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
		request.Headers.Host = "needs-setup.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var content = await response.Content.ReadAsStringAsync();
		Assert.Contains("true", content, StringComparison.OrdinalIgnoreCase);
	}
}
