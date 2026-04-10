using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.ExternalLogin)]
public class ExternalLoginTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public ExternalLoginTests(SharedPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		var connectionString = await _fixture.CreateIsolatedDatabasesAsync();
		_factory = new CocoarAuthWebApplicationFactory(connectionString);
		_client = _factory.CreateClientWithCookies();
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

	private async Task CreateOidcProviderAsync()
	{
		await LoginAsAdminAsync();
		var createDto = new CreateLoginProviderDto
		{
			Name = "TestGoogle",
			DisplayName = "Google (Test)",
			Description = "Test OIDC Provider",
			Type = LoginProviderType.OpenIdConnect,
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://accounts.google.com",
				["ClientId"] = "test-client-id",
				["ClientSecret"] = "test-client-secret",
				["Scopes"] = "openid profile email"
			}
		};

		await _client.PostAsJsonAsync("/api/admin/login-providers", createDto, _factory.JsonOptions);
	}

	[Fact]
	public async Task GetExternalProviders_NoOidcProviders_ReturnsEmptyList()
	{
		// Act
		var response = await _client.GetAsync("/api/auth/external-providers");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ExternalProviderListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Empty(result.Providers);
	}

	[Fact]
	public async Task GetExternalProviders_WithOidcProvider_ReturnsProviders()
	{
		// Arrange
		await CreateOidcProviderAsync();

		// Logout admin before testing public endpoint
		await _client.PostAsync("/api/auth/logout", null);

		// Act
		var response = await _client.GetAsync("/api/auth/external-providers");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ExternalProviderListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Single(result.Providers);
		Assert.Equal("TestGoogle", result.Providers[0].Name);
		Assert.Equal("Google (Test)", result.Providers[0].DisplayName);
		Assert.Equal("OpenIdConnect", result.Providers[0].Type);
	}

	[Fact]
	public async Task GetExternalProviders_DoesNotExposeSecrets()
	{
		// Arrange
		await CreateOidcProviderAsync();

		// Act
		var response = await _client.GetAsync("/api/auth/external-providers");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("test-client-secret", body);
		Assert.DoesNotContain("ClientSecret", body);
	}

	[Fact]
	public async Task ExternalLogin_InvalidProvider_ReturnsBadRequest()
	{
		// Act
		var response = await _client.GetAsync("/api/auth/external-login?provider=nonexistent&returnUrl=/");

		// Assert
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task ExternalLogin_InternalProvider_ReturnsBadRequest()
	{
		// Act - Internal provider is not OIDC
		var response = await _client.GetAsync("/api/auth/external-login?provider=Internal&returnUrl=/");

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task ExternalLogin_ValidProvider_ReturnsRedirect()
	{
		// Arrange
		await CreateOidcProviderAsync();

		// Use a new client to avoid admin session interfering
		using var anonClient = _factory.CreateClientWithCookies();

		// Act
		var response = await anonClient.GetAsync(
			"/api/auth/external-login?provider=TestGoogle&returnUrl=/dashboard");

		// Assert - should redirect to Google's authorization endpoint
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location?.ToString();
		Assert.NotNull(location);
		Assert.Contains("accounts.google.com", location);
		Assert.Contains("response_type=code", location);
		Assert.Contains("client_id=test-client-id", location);
		Assert.Contains("code_challenge_method=S256", location);
		Assert.Contains("scope=", location);
		Assert.Contains("state=", location);
		Assert.Contains("nonce=", location);
	}

	[Fact]
	public async Task ExternalCallback_InvalidState_RedirectsWithError()
	{
		// Act
		var response = await _client.GetAsync(
			"/api/auth/external-callback?code=fake&state=invalid-state");

		// Assert
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location?.ToString();
		Assert.NotNull(location);
		Assert.Contains("error=external_login_failed", location);
	}

	[Fact]
	public async Task GetLinkedExternalLogins_Unauthenticated_Returns401()
	{
		// Act
		var response = await _client.GetAsync("/api/auth/external-logins");

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetLinkedExternalLogins_Authenticated_ReturnsEmptyList()
	{
		// Arrange
		await _factory.CreateTestUserAsync("testuser", "Test123!@#");
		await _client.LoginAsync("testuser", "Test123!@#", _factory.JsonOptions);

		// Act
		var response = await _client.GetAsync("/api/auth/external-logins");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<LinkedExternalLoginListDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Empty(result.Logins);
	}

	[Fact]
	public async Task UnlinkExternalLogin_NoExternalLogins_ReturnsNotFound()
	{
		// Arrange
		await _factory.CreateTestUserAsync("testuser", "Test123!@#");
		await _client.LoginAsync("testuser", "Test123!@#", _factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync("/api/auth/external-link/google");

		// Assert
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task UnlinkExternalLogin_Unauthenticated_Returns401()
	{
		// Act
		var response = await _client.DeleteAsync("/api/auth/external-link/google");

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task ExternalLink_Unauthenticated_Returns401()
	{
		// Act
		var response = await _client.PostAsync(
			"/api/auth/external-link?provider=google&returnUrl=/", null);

		// Assert
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}
}
