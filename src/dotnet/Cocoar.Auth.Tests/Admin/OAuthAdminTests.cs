using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class OAuthAdminTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public OAuthAdminTests(SharedPostgresFixture fixture)
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

	#region OAuth Clients

	[Fact]
	public async Task GetClients_WithoutAuthentication_ReturnsUnauthorized()
	{
		// Act
		var response = await _client.GetAsync("/api/admin/oauth/clients");

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetClients_WithNonAdminUser_ReturnsForbidden()
	{
		// Arrange
		var user = await _factory.CreateTestUserAsync(isAdmin: false);
		await _client.LoginAsync(user.UserName, "Test123!@#", _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/api/admin/oauth/clients");

		// Assert
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateClient_WithValidData_ReturnsCreatedClient()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "test-client",
			DisplayName = "Test Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = new List<string> { "http://localhost/callback" },
			PostLogoutRedirectUris = new List<string> { "http://localhost" },
			Scopes = new List<string> { "openid", "profile" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("test-client", result.Client.ClientId);
		Assert.Equal("Test Client", result.Client.DisplayName);
		Assert.Equal("public", result.Client.ClientType);
		Assert.Null(result.ClientSecret); // Public clients don't have secrets
	}

	[Fact]
	public async Task CreateClient_ConfidentialType_ReturnsClientSecret()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "confidential-client",
			DisplayName = "Confidential Client",
			ClientType = "confidential",
			ConsentType = "explicit",
			ClientSecret = "TestSecret123!", // Provide explicit secret
			RedirectUris = new List<string> { "http://localhost/callback" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.ClientSecret);
		Assert.Equal("TestSecret123!", result.ClientSecret); // Returns the provided secret
	}

	[Fact]
	public async Task CreateClient_WithDuplicateClientId_ReturnsConflict()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "duplicate-client",
			ClientType = "public",
			ConsentType = "implicit"
		};

		// Create first client
		await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Act - try to create duplicate
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task GetClients_AsAdmin_ReturnsClientList()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "list-test-client",
			ClientType = "public",
			ConsentType = "implicit"
		};
		await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/api/admin/oauth/clients");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.TotalCount >= 1);
		Assert.Contains(result.Items, c => c.ClientId == "list-test-client");
	}

	[Fact]
	public async Task UpdateClient_WithValidData_ReturnsUpdatedClient()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "update-test",
			DisplayName = "Original Name",
			ClientType = "public",
			ConsentType = "implicit"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);

		var updateDto = new UpdateOAuthClientDto
		{
			DisplayName = "Updated Name",
			ConsentType = "explicit"
		};

		// Act
		var response = await _client.PutAsJsonAsync($"/api/admin/oauth/clients/{created!.Client.Id}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Name", result.DisplayName);
		Assert.Equal("explicit", result.ConsentType);
	}

	[Fact]
	public async Task DeleteClient_WithValidId_ReturnsNoContent()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "delete-test",
			ClientType = "public",
			ConsentType = "implicit"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync($"/api/admin/oauth/clients/{created!.Client.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify deleted
		var getResponse = await _client.GetAsync($"/api/admin/oauth/clients/{created.Client.Id}");
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
	}

	[Fact]
	public async Task RegenerateClientSecret_ForConfidentialClient_ReturnsNewSecret()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "regen-secret-test",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "OriginalSecret123!" // Provide explicit initial secret
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.PostAsync($"/api/admin/oauth/clients/{created!.Client.Id}/regenerate-secret", null);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ClientSecretDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.ClientSecret);
		Assert.NotEqual(created.ClientSecret, result.ClientSecret); // New secret should be different
	}

	[Fact]
	public async Task RegenerateClientSecret_ForPublicClient_ReturnsBadRequest()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "public-no-secret",
			ClientType = "public",
			ConsentType = "implicit"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.PostAsync($"/api/admin/oauth/clients/{created!.Client.Id}/regenerate-secret", null);

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	#endregion

	#region OAuth Scopes

	[Fact]
	public async Task GetScopes_AsAdmin_ReturnsScopeList()
	{
		// Arrange
		await LoginAsAdminAsync();
		await _factory.SeedOpenIddictScopesAsync();

		// Act
		var response = await _client.GetAsync("/api/admin/oauth/scopes");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthScopeListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		// Should have standard OIDC scopes seeded
		Assert.Contains(result.Items, s => s.Name == "openid");
		Assert.Contains(result.Items, s => s.Name == "profile");
		Assert.Contains(result.Items, s => s.Name == "email");
	}

	[Fact]
	public async Task CreateScope_WithValidData_ReturnsCreatedScope()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthScopeDto
		{
			Name = "custom-scope",
			DisplayName = "Custom Scope",
			Description = "A custom scope for testing",
			Resources = new List<string> { "my-api" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/scopes", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthScopeDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("custom-scope", result.Name);
		Assert.Equal("Custom Scope", result.DisplayName);
		Assert.Contains("my-api", result.Resources);
	}

	[Fact]
	public async Task CreateScope_WithDuplicateName_ReturnsConflict()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthScopeDto
		{
			Name = "duplicate-scope"
		};

		// Create first
		await _client.PostAsJsonAsync("/api/admin/oauth/scopes", createDto, _factory.JsonOptions);

		// Act - try duplicate
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/scopes", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task DeleteScope_StandardScope_ReturnsBadRequest()
	{
		// Arrange
		await LoginAsAdminAsync();
		await _factory.SeedOpenIddictScopesAsync();

		// Get the openid scope
		var scopesResponse = await _client.GetAsync("/api/admin/oauth/scopes");
		var scopes = await scopesResponse.ReadFromJsonAsync<OAuthScopeListDto>(_factory.JsonOptions);
		var openIdScope = scopes!.Items.First(s => s.Name == "openid");

		// Act
		var response = await _client.DeleteAsync($"/api/admin/oauth/scopes/{openIdScope.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task DeleteScope_CustomScope_ReturnsNoContent()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthScopeDto { Name = "deletable-scope" };
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/scopes", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthScopeDto>(_factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync($"/api/admin/oauth/scopes/{created!.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	#endregion

	#region OAuth API Resources

	[Fact]
	public async Task GetApiResources_WithoutAuthentication_ReturnsUnauthorized()
	{
		// Act
		var response = await _client.GetAsync("/api/admin/oauth/api-resources");

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetApiResources_WithNonAdminUser_ReturnsForbidden()
	{
		// Arrange
		var user = await _factory.CreateTestUserAsync(isAdmin: false);
		await _client.LoginAsync(user.UserName, "Test123!@#", _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/api/admin/oauth/api-resources");

		// Assert
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateApiResource_WithValidData_ReturnsCreatedWithSecret()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "my-api",
			DisplayName = "My API",
			Description = "Test API resource",
			Enabled = true,
			Scopes = new List<string> { "openid", "profile" },
			UserClaims = new List<string> { "email", "name" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("my-api", result.Name);
		Assert.Equal("My API", result.DisplayName);
		Assert.True(result.Enabled);
		Assert.NotNull(result.ApiSecret);
		Assert.NotEmpty(result.ApiSecret);
		Assert.Contains("openid", result.Scopes);
		Assert.Contains("email", result.UserClaims);
	}

	[Fact]
	public async Task CreateApiResource_WithDuplicateName_ReturnsConflict()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "duplicate-api",
			Enabled = true
		};

		// Create first
		await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);

		// Act - try duplicate
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task GetApiResources_AsAdmin_ReturnsResourceList()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "list-test-api",
			DisplayName = "List Test API"
		};
		await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/api/admin/oauth/api-resources");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.TotalCount >= 1);
		Assert.Contains(result.Items, r => r.Name == "list-test-api");
	}

	[Fact]
	public async Task GetApiResource_WithValidId_ReturnsResource()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "get-test-api",
			DisplayName = "Get Test API"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.GetAsync($"/api/admin/oauth/api-resources/{created!.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("get-test-api", result.Name);
	}

	[Fact]
	public async Task GetApiResource_WithInvalidId_ReturnsNotFound()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Act
		var response = await _client.GetAsync($"/api/admin/oauth/api-resources/{Guid.NewGuid()}");

		// Assert
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task UpdateApiResource_WithValidData_ReturnsUpdatedResource()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "update-test-api",
			DisplayName = "Original Name",
			Enabled = true
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);

		var updateDto = new UpdateOAuthApiResourceDto
		{
			DisplayName = "Updated Name",
			Description = "New description",
			Enabled = false,
			Scopes = new List<string> { "email" },
			UserClaims = new List<string> { "sub", "email" }
		};

		// Act
		var response = await _client.PutAsJsonAsync($"/api/admin/oauth/api-resources/{created!.Id}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Name", result.DisplayName);
		Assert.Equal("New description", result.Description);
		Assert.False(result.Enabled);
		Assert.Contains("email", result.Scopes);
		Assert.Contains("sub", result.UserClaims);
	}

	[Fact]
	public async Task DeleteApiResource_WithValidId_ReturnsNoContent()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "delete-test-api"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync($"/api/admin/oauth/api-resources/{created!.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify deleted
		var getResponse = await _client.GetAsync($"/api/admin/oauth/api-resources/{created.Id}");
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
	}

	[Fact]
	public async Task DeleteApiResource_WithNonExistentId_ReturnsNotFound()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Act
		var response = await _client.DeleteAsync($"/api/admin/oauth/api-resources/{Guid.NewGuid()}");

		// Assert
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task RegenerateApiSecret_WithValidId_ReturnsNewSecret()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = "regen-secret-api"
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);
		var originalSecret = created!.ApiSecret;

		// Act
		var response = await _client.PostAsync($"/api/admin/oauth/api-resources/{created.Id}/regenerate-secret", null);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ApiSecretDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.ApiSecret);
		Assert.NotEmpty(result.ApiSecret);
		Assert.NotEqual(originalSecret, result.ApiSecret);
	}

	[Fact]
	public async Task RegenerateApiSecret_WithNonExistentId_ReturnsNotFound()
	{
		// Arrange
		await LoginAsAdminAsync();

		// Act
		var response = await _client.PostAsync($"/api/admin/oauth/api-resources/{Guid.NewGuid()}/regenerate-secret", null);

		// Assert
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	#endregion
}
