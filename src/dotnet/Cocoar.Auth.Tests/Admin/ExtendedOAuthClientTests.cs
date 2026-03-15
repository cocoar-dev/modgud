using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class ExtendedOAuthClientTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public ExtendedOAuthClientTests(SharedPostgresFixture fixture)
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
	public async Task Create_WithReferenceTokenType_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "ref-token-client",
			DisplayName = "Reference Token Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			AccessTokenType = AccessTokenType.Reference
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal(AccessTokenType.Reference, result.Client.AccessTokenType);
	}

	[Fact]
	public async Task Create_WithJwtTokenType_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "jwt-token-client",
			DisplayName = "JWT Token Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			AccessTokenType = AccessTokenType.Jwt
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal(AccessTokenType.Jwt, result.Client.AccessTokenType);
	}

	[Fact]
	public async Task Create_WithGrantTypes_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "grant-types-client",
			DisplayName = "Grant Types Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			AllowedGrantTypes = new List<string> { "authorization_code", "client_credentials" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Contains("authorization_code", result.Client.AllowedGrantTypes);
		Assert.Contains("client_credentials", result.Client.AllowedGrantTypes);
	}

	[Fact]
	public async Task Create_WithLifetimeOptions_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "lifetime-client",
			DisplayName = "Lifetime Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			IdentityTokenLifetime = 300,
			AccessTokenLifetime = 3600,
			AuthorizationCodeLifetime = 300,
			AbsoluteRefreshTokenLifetime = 2592000,
			SlidingRefreshTokenLifetime = 1296000
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal(300, result.Client.IdentityTokenLifetime);
		Assert.Equal(3600, result.Client.AccessTokenLifetime);
		Assert.Equal(300, result.Client.AuthorizationCodeLifetime);
		Assert.Equal(2592000, result.Client.AbsoluteRefreshTokenLifetime);
		Assert.Equal(1296000, result.Client.SlidingRefreshTokenLifetime);
	}

	[Fact]
	public async Task Create_WithCorsOrigins_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "cors-client",
			DisplayName = "CORS Client",
			ClientType = "public",
			ConsentType = "implicit",
			AllowedCorsOrigins = new List<string> { "http://localhost:3000", "https://app.example.com" }
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Contains("http://localhost:3000", result.Client.AllowedCorsOrigins);
		Assert.Contains("https://app.example.com", result.Client.AllowedCorsOrigins);
	}

	[Fact]
	public async Task Create_WithClientClaims_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "claims-client",
			DisplayName = "Claims Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			AlwaysSendClientClaims = true,
			ClientClaimsPrefix = "client_",
			Claims = new List<OAuthClientClaimDto>
			{
				new() { Type = "tenant_id", Value = "tenant-1" },
				new() { Type = "app_version", Value = "2.0" }
			}
		};

		// Act
		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.True(result.Client.AlwaysSendClientClaims);
		Assert.Equal("client_", result.Client.ClientClaimsPrefix);
		Assert.Equal(2, result.Client.Claims.Count);
		Assert.Contains(result.Client.Claims, c => c.Type == "tenant_id" && c.Value == "tenant-1");
		Assert.Contains(result.Client.Claims, c => c.Type == "app_version" && c.Value == "2.0");
	}

	[Fact]
	public async Task Update_AllExtendedFields_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "update-extended-client",
			DisplayName = "Original Client",
			ClientType = "confidential",
			ConsentType = "implicit",
			ClientSecret = "Secret123!",
			AccessTokenType = AccessTokenType.Reference
		};
		var createResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		var created = await createResponse.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);

		var updateDto = new UpdateOAuthClientDto
		{
			DisplayName = "Updated Client",
			ConsentType = "explicit",
			AccessTokenType = AccessTokenType.Jwt,
			Enabled = false,
			RefreshTokenUsage = RefreshTokenUsage.ReUse,
			AllowAccessTokensViaBrowser = true,
			RequireClientSecret = false,
			EnableLocalLogin = false,
			RequireConsent = true,
			AllowRememberConsent = false,
			AllowedGrantTypes = new List<string> { "authorization_code" },
			AllowedCorsOrigins = new List<string> { "https://updated.example.com" },
			IdentityTokenLifetime = 600,
			AccessTokenLifetime = 7200,
			AuthorizationCodeLifetime = 600,
			AbsoluteRefreshTokenLifetime = 5184000,
			SlidingRefreshTokenLifetime = 2592000,
			AlwaysSendClientClaims = true,
			UpdateAccessTokenClaimsOnRefresh = true,
			ClientClaimsPrefix = "updated_",
			Claims = new List<OAuthClientClaimDto>
			{
				new() { Type = "updated_claim", Value = "updated_value" }
			}
		};

		// Act
		var response = await _client.PutAsJsonAsync(
			$"/api/admin/oauth/clients/{created!.Client.Id}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("Updated Client", result.DisplayName);
		Assert.Equal("explicit", result.ConsentType);
		Assert.Equal(AccessTokenType.Jwt, result.AccessTokenType);
		Assert.False(result.Enabled);
		Assert.Equal(RefreshTokenUsage.ReUse, result.RefreshTokenUsage);
		Assert.True(result.AllowAccessTokensViaBrowser);
		Assert.False(result.RequireClientSecret);
		Assert.False(result.EnableLocalLogin);
		Assert.True(result.RequireConsent);
		Assert.False(result.AllowRememberConsent);
		Assert.Contains("authorization_code", result.AllowedGrantTypes);
		Assert.Contains("https://updated.example.com", result.AllowedCorsOrigins);
		Assert.Equal(600, result.IdentityTokenLifetime);
		Assert.Equal(7200, result.AccessTokenLifetime);
		Assert.Equal(600, result.AuthorizationCodeLifetime);
		Assert.Equal(5184000, result.AbsoluteRefreshTokenLifetime);
		Assert.Equal(2592000, result.SlidingRefreshTokenLifetime);
		Assert.True(result.AlwaysSendClientClaims);
		Assert.True(result.UpdateAccessTokenClaimsOnRefresh);
		Assert.Equal("updated_", result.ClientClaimsPrefix);
		Assert.Single(result.Claims);
		Assert.Equal("updated_claim", result.Claims[0].Type);
		Assert.Equal("updated_value", result.Claims[0].Value);
	}
}
