using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class ExtendedApiResourceTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public ExtendedApiResourceTests(SharedPostgresFixture fixture)
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

	/// <summary>
	/// Helper: creates an API resource and returns its created DTO.
	/// </summary>
	private async Task<OAuthApiResourceCreatedDto> CreateApiResourceAsync(string name)
	{
		var createDto = new CreateOAuthApiResourceDto
		{
			Name = name,
			DisplayName = $"API: {name}",
			Enabled = true,
			Scopes = new List<string> { "openid" }
		};
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/api-resources", createDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		return result;
	}

	[Fact]
	public async Task CreateSecret_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var apiResource = await CreateApiResourceAsync("secret-create-api");

		var secretDto = new CreateApiSecretDto
		{
			Type = "SharedSecret",
			Description = "Test secret",
			Expiration = DateTimeOffset.UtcNow.AddYears(1)
		};

		// Act
		var response = await _client.PostAsJsonAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets", secretDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ApiSecretCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.SecretId);
		Assert.NotEmpty(result.SecretId);
		Assert.NotNull(result.ApiSecret);
		Assert.NotEmpty(result.ApiSecret);
	}

	[Fact]
	public async Task CreateSecret_ReturnsPlaintext()
	{
		// Arrange
		await LoginAsAdminAsync();
		var apiResource = await CreateApiResourceAsync("secret-plaintext-api");

		var secretDto = new CreateApiSecretDto
		{
			Description = "Plaintext test"
		};

		// Act
		var response = await _client.PostAsJsonAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets", secretDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<ApiSecretCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.ApiSecret);
		// The plaintext secret should be a non-trivial string
		Assert.True(result.ApiSecret.Length > 10);

		// Verify the secret is NOT returned when fetching the API resource details
		var getResponse = await _client.GetAsync($"/api/admin/oauth/api-resources/{apiResource.Id}");
		var apiDto = await getResponse.ReadFromJsonAsync<OAuthApiResourceDto>(_factory.JsonOptions);
		Assert.NotNull(apiDto);
		// Secret metadata should be present but not plaintext values
		var secretEntry = apiDto.Secrets.FirstOrDefault(s => s.SecretId == result.SecretId);
		Assert.NotNull(secretEntry);
		Assert.Equal("Plaintext test", secretEntry.Description);
	}

	[Fact]
	public async Task DeleteSecret_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var apiResource = await CreateApiResourceAsync("secret-delete-api");

		// Create a secret
		var secretDto = new CreateApiSecretDto { Description = "Delete me" };
		var createResponse = await _client.PostAsJsonAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets", secretDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<ApiSecretCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.DeleteAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets/{created!.SecretId}");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		// Verify the secret is gone
		var getResponse = await _client.GetAsync($"/api/admin/oauth/api-resources/{apiResource.Id}");
		var apiDto = await getResponse.ReadFromJsonAsync<OAuthApiResourceDto>(_factory.JsonOptions);
		Assert.NotNull(apiDto);
		Assert.DoesNotContain(apiDto.Secrets, s => s.SecretId == created.SecretId);
	}

	[Fact]
	public async Task GetApiResource_IncludesSecretMetadata()
	{
		// Arrange
		await LoginAsAdminAsync();
		var apiResource = await CreateApiResourceAsync("secret-metadata-api");

		// Create two secrets
		var secret1Dto = new CreateApiSecretDto
		{
			Description = "First secret",
			Expiration = DateTimeOffset.UtcNow.AddMonths(6)
		};
		var secret2Dto = new CreateApiSecretDto
		{
			Description = "Second secret"
		};
		var create1 = await _client.PostAsJsonAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets", secret1Dto, _factory.JsonOptions);
		var created1 = await create1.ReadFromJsonAsync<ApiSecretCreatedDto>(_factory.JsonOptions);

		var create2 = await _client.PostAsJsonAsync(
			$"/api/admin/oauth/api-resources/{apiResource.Id}/secrets", secret2Dto, _factory.JsonOptions);
		var created2 = await create2.ReadFromJsonAsync<ApiSecretCreatedDto>(_factory.JsonOptions);

		// Act
		var response = await _client.GetAsync($"/api/admin/oauth/api-resources/{apiResource.Id}");

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthApiResourceDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		// Should include secret metadata (the initial secret from creation + our 2 new secrets)
		Assert.True(result.Secrets.Count >= 2);

		var firstSecret = result.Secrets.FirstOrDefault(s => s.SecretId == created1!.SecretId);
		Assert.NotNull(firstSecret);
		Assert.Equal("First secret", firstSecret.Description);
		Assert.Equal("SharedSecret", firstSecret.Type);
		Assert.NotNull(firstSecret.Expiration);

		var secondSecret = result.Secrets.FirstOrDefault(s => s.SecretId == created2!.SecretId);
		Assert.NotNull(secondSecret);
		Assert.Equal("Second secret", secondSecret.Description);
		Assert.Null(secondSecret.Expiration);
	}
}
