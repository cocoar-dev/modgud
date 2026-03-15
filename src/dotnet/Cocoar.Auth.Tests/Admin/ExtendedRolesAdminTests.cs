using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class ExtendedRolesAdminTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public ExtendedRolesAdminTests(SharedPostgresFixture fixture)
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
	public async Task Create_WithDisplayNameAndEmail_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateRoleDto
		{
			Name = "SupportTeam",
			Description = "Support team role",
			DisplayName = "Support Team",
			Email = "support@example.com"
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/roles", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("SupportTeam", result.Name);
		Assert.Equal("Support Team", result.DisplayName);
		Assert.Equal("support@example.com", result.Email);
	}

	[Fact]
	public async Task Create_WithBoundToApiResource_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Create an API resource first
		var apiResourceDto = new CreateOAuthApiResourceDto
		{
			Name = "bound-api",
			DisplayName = "Bound API",
			Enabled = true
		};
		var apiResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", apiResourceDto, _factory.JsonOptions);
		var apiResource = await apiResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);
		var apiResourceId = new ShortGuid(Guid.Parse(apiResource!.Id));

		var createDto = new CreateRoleDto
		{
			Name = "ApiRole",
			Description = "Role bound to API resource",
			BoundToApiResourceId = apiResourceId
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/roles", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("ApiRole", result.Name);
		Assert.Equal(apiResourceId.Value, result.BoundToApiResourceId!.Value.Value);
	}

	[Fact]
	public async Task Update_DisplayNameAndEmail_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var role = await _factory.CreateTestRoleAsync("UpdateExtended", "Original");
		var shortGuid = new ShortGuid(role.Id);

		var updateDto = new
		{
			DisplayName = "Updated Display Name",
			Email = "updated@example.com"
		};

		// Act
		var response = await _client.PatchAsJsonAsync($"/api/admin/roles/{shortGuid}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Display Name", result.DisplayName);
		Assert.Equal("updated@example.com", result.Email);
	}

	[Fact]
	public async Task Update_BoundToApiResource_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var role = await _factory.CreateTestRoleAsync("BindRole", "Bind to API");
		var shortGuid = new ShortGuid(role.Id);

		// Create an API resource
		var apiResourceDto = new CreateOAuthApiResourceDto
		{
			Name = "bind-api",
			DisplayName = "Bind API",
			Enabled = true
		};
		var apiResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", apiResourceDto, _factory.JsonOptions);
		var apiResource = await apiResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);
		var apiResourceId = new ShortGuid(Guid.Parse(apiResource!.Id));

		var updateDto = new { BoundToApiResourceId = apiResourceId };

		// Act
		var response = await _client.PatchAsJsonAsync($"/api/admin/roles/{shortGuid}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal(apiResourceId.Value, result.BoundToApiResourceId!.Value.Value);
	}
}
