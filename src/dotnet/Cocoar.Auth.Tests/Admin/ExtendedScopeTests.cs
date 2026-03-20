using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class ExtendedScopeTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public ExtendedScopeTests(SharedPostgresFixture fixture)
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
	public async Task Create_WithIdentityResourceProperties_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthScopeDto
		{
			Name = "extended-scope",
			DisplayName = "Extended Scope",
			Description = "A scope with all identity resource properties",
			Enabled = true,
			Required = true,
			Emphasize = true,
			ShowInDiscoveryDocument = false,
			UserClaims = new List<string> { "email", "name", "preferred_username" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/system/api/admin/oauth/scopes", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthScopeDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("extended-scope", result.Name);
		Assert.Equal("Extended Scope", result.DisplayName);
		Assert.Equal("A scope with all identity resource properties", result.Description);
		Assert.True(result.Enabled);
		Assert.True(result.Required);
		Assert.True(result.Emphasize);
		Assert.False(result.ShowInDiscoveryDocument);
		Assert.Equal(3, result.UserClaims.Count);
		Assert.Contains("email", result.UserClaims);
		Assert.Contains("name", result.UserClaims);
		Assert.Contains("preferred_username", result.UserClaims);
	}

	[Fact]
	public async Task Update_IdentityResourceProperties_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthScopeDto
		{
			Name = "update-extended-scope",
			DisplayName = "Original Scope",
			Enabled = true,
			Required = false,
			Emphasize = false,
			ShowInDiscoveryDocument = true,
			UserClaims = new List<string> { "sub" }
		};
		var createResponse = await _client.PostAsJsonAsync("/system/api/admin/oauth/scopes", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthScopeDto>(_factory.JsonOptions);

		var updateDto = new UpdateOAuthScopeDto
		{
			DisplayName = "Updated Scope",
			Description = "Updated description",
			Enabled = false,
			Required = true,
			Emphasize = true,
			ShowInDiscoveryDocument = false,
			UserClaims = new List<string> { "email", "name" }
		};

		// Act
		var response = await _client.PutAsJsonAsync(
			$"/system/api/admin/oauth/scopes/{created!.Id}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthScopeDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Scope", result.DisplayName);
		Assert.Equal("Updated description", result.Description);
		Assert.False(result.Enabled);
		Assert.True(result.Required);
		Assert.True(result.Emphasize);
		Assert.False(result.ShowInDiscoveryDocument);
		Assert.Equal(2, result.UserClaims.Count);
		Assert.Contains("email", result.UserClaims);
		Assert.Contains("name", result.UserClaims);
	}
}
