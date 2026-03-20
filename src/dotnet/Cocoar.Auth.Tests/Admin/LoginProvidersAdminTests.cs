using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class LoginProvidersAdminTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public LoginProvidersAdminTests(SharedPostgresFixture fixture)
	{
		_factory = new CocoarAuthWebApplicationFactory(fixture);
		_client = _factory.CreateClientWithCookies();
	}

	public async Task InitializeAsync()
	{
		await _factory.CleanDatabaseAsync();
		await _factory.SeedLoginProvidersAsync();
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
	public async Task GetAll_AsAdmin_ReturnsProviders()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Act
		var response = await _client.GetAsync("/system/api/admin/login-providers");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<LoginProviderListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.TotalCount >= 1);
		Assert.Contains(result.Items, p => p.Name == "Internal" && p.IsBuiltIn);
	}

	[Fact]
	public async Task GetById_AsAdmin_ReturnsProvider()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Get the seeded Internal provider
		var listResponse = await _client.GetAsync("/system/api/admin/login-providers");
		var list = await listResponse.ReadFromJsonAsync<LoginProviderListDto>(_factory.JsonOptions);
		var internalProvider = list!.Items.First(p => p.Name == "Internal");

		// Act
		var response = await _client.GetAsync($"/system/api/admin/login-providers/{internalProvider.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<LoginProviderDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Internal", result.Name);
		Assert.Equal(LoginProviderType.Internal, result.Type);
		Assert.True(result.IsBuiltIn);
	}

	[Fact]
	public async Task Create_AsAdmin_CreatesProvider()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateLoginProviderDto
		{
			Name = "TestOIDC",
			DisplayName = "Test OIDC Provider",
			Description = "An OpenID Connect test provider",
			Type = LoginProviderType.OpenIdConnect,
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://login.example.com",
				["ClientId"] = "test-client-id"
			}
		};

		// Act
		var response = await _client.PostAsJsonAsync("/system/api/admin/login-providers", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<LoginProviderDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("TestOIDC", result.Name);
		Assert.Equal("Test OIDC Provider", result.DisplayName);
		Assert.Equal("An OpenID Connect test provider", result.Description);
		Assert.Equal(LoginProviderType.OpenIdConnect, result.Type);
		Assert.Equal("https://login.example.com", result.Configuration["Authority"]);
		Assert.Equal("test-client-id", result.Configuration["ClientId"]);
		Assert.False(result.IsBuiltIn);
	}

	[Fact]
	public async Task Update_AsAdmin_UpdatesProvider()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateLoginProviderDto
		{
			Name = "UpdateMe",
			DisplayName = "Original Name",
			Type = LoginProviderType.OpenIdConnect,
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://old.example.com"
			}
		};
		var createResponse = await _client.PostAsJsonAsync("/system/api/admin/login-providers", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<LoginProviderDto>(_factory.JsonOptions);

		var updateDto = new UpdateLoginProviderDto
		{
			DisplayName = "Updated Name",
			Description = "Updated description",
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://new.example.com",
				["ClientId"] = "new-client-id"
			}
		};

		// Act
		var response = await _client.PatchAsJsonAsync(
			$"/system/api/admin/login-providers/{created!.Id}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<LoginProviderDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Name", result.DisplayName);
		Assert.Equal("Updated description", result.Description);
		Assert.Equal("https://new.example.com", result.Configuration["Authority"]);
		Assert.Equal("new-client-id", result.Configuration["ClientId"]);
	}

	[Fact]
	public async Task Delete_AsAdmin_DeletesProvider()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateLoginProviderDto
		{
			Name = "DeleteMe",
			Type = LoginProviderType.OpenIdConnect,
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://delete.example.com"
			}
		};
		var createResponse = await _client.PostAsJsonAsync("/system/api/admin/login-providers", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<LoginProviderDto>(_factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync($"/system/api/admin/login-providers/{created!.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify deleted
		var getResponse = await _client.GetAsync($"/system/api/admin/login-providers/{created.Id}");
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
	}

	[Fact]
	public async Task Delete_BuiltInProvider_Fails()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Get the seeded Internal provider
		var listResponse = await _client.GetAsync("/system/api/admin/login-providers");
		var list = await listResponse.ReadFromJsonAsync<LoginProviderListDto>(_factory.JsonOptions);
		var internalProvider = list!.Items.First(p => p.Name == "Internal" && p.IsBuiltIn);

		// Act
		var response = await _client.DeleteAsync($"/system/api/admin/login-providers/{internalProvider.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task GetAll_Unauthenticated_Returns401()
	{
		// Act
		var response = await _client.GetAsync("/system/api/admin/login-providers");

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetAll_NonAdmin_Returns403()
	{
		// Arrange
		var user = await _factory.CreateTestUserAsync(isAdmin: false);
		await _client.LoginAsync(user.UserName, "Test123!@#", _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/system/api/admin/login-providers");

		// Assert
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}
}
