using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.OAuth)]
public class TokenClaimsTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public TokenClaimsTests(SharedPostgresFixture fixture)
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

	#region Private DTOs

	private record TokenResponse
	{
		[JsonPropertyName("access_token")]
		public string? AccessToken { get; init; }

		[JsonPropertyName("token_type")]
		public string? TokenType { get; init; }

		[JsonPropertyName("expires_in")]
		public int ExpiresIn { get; init; }

		[JsonPropertyName("id_token")]
		public string? IdToken { get; init; }

		[JsonPropertyName("refresh_token")]
		public string? RefreshToken { get; init; }

		[JsonPropertyName("scope")]
		public string? Scope { get; init; }
	}

	#endregion

	#region Helper Methods

	private static (string codeVerifier, string codeChallenge) GeneratePkce()
	{
		var bytes = new byte[32];
		RandomNumberGenerator.Fill(bytes);
		var codeVerifier = Base64UrlEncode(bytes);

		var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
		var codeChallenge = Base64UrlEncode(hash);

		return (codeVerifier, codeChallenge);
	}

	private static string Base64UrlEncode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private async Task<OAuthClientCreatedDto> CreateOAuthClientViaAdminAsync(CreateOAuthClientDto createDto)
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
		await _factory.SeedOpenIddictScopesAsync();

		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);

		// Logout admin
		await _client.PostAsync("/api/auth/logout", null);

		return result;
	}

	private async Task<string> GetAuthorizationCodeAsync(
		string clientId, string redirectUri, string codeChallenge, string scope, string state)
	{
		var query = $"?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
			$"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
			$"&scope={Uri.EscapeDataString(scope)}" +
			$"&state={Uri.EscapeDataString(state)}" +
			$"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
			$"&code_challenge_method=S256";

		var response = await _client.GetAsync($"/connect/authorize{query}");

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location!;
		var queryParams = System.Web.HttpUtility.ParseQueryString(location.Query);
		var code = queryParams["code"];
		Assert.NotNull(code);
		return code;
	}

	private async Task<TokenResponse> ExchangeCodeForTokensAsync(
		string code, string clientId, string redirectUri, string codeVerifier, string? clientSecret = null)
	{
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["client_id"] = clientId,
			["redirect_uri"] = redirectUri,
			["code_verifier"] = codeVerifier
		};

		if (clientSecret != null)
		{
			parameters["client_secret"] = clientSecret;
		}

		var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(parameters));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokenResponse);
		return tokenResponse;
	}

	private async Task<ApplicationUser> CreateTestUserWithRolesAndClaimsAsync(
		string userName, string password, string email,
		List<string>? roles = null,
		List<(string Type, string Value)>? customClaims = null)
	{
		var user = await _factory.CreateTestUserAsync(userName, password, email: email);

		using var scope = _factory.Services.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

		// Add roles
		if (roles != null)
		{
			foreach (var roleName in roles)
			{
				var existingRole = await roleManager.FindByNameAsync(roleName);
				if (existingRole is null)
				{
					existingRole = new ApplicationRole(roleName, $"{roleName} role");
					await roleManager.CreateAsync(existingRole);
				}
				await userManager.AddToRoleAsync(user, roleName);
			}
		}

		// Add custom claims
		if (customClaims != null)
		{
			var claims = customClaims.Select(c => new Claim(c.Type, c.Value)).ToList();
			await userManager.AddClaimsAsync(user, claims);
		}

		return user;
	}

	private async Task<JsonElement> IntrospectTokenAsync(string token, string clientId, string clientSecret)
	{
		var parameters = new Dictionary<string, string>
		{
			["token"] = token,
			["client_id"] = clientId,
			["client_secret"] = clientSecret
		};

		var response = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(parameters));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	#endregion

	#region Access Token Claims Tests

	[Fact]
	public async Task AccessToken_ContainsRoleClaims()
	{
		// Arrange - create confidential client requesting roles scope
		// Using a confidential client so we can introspect the reference token
		var clientSecret = "RoleClaimsSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "role-claims-client",
			DisplayName = "Role Claims Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile", "roles"]
		});

		// Create user with Admin role
		await CreateTestUserWithRolesAndClaimsAsync(
			"roleuser", "Test123!@#", "roleuser@test.com",
			roles: ["Admin"]);

		await _client.LoginAsync("roleuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"role-claims-client", "http://localhost/callback", codeChallenge,
			"openid profile roles", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "role-claims-client", "http://localhost/callback", codeVerifier, clientSecret);

		// Assert - introspect the reference token to check for role claim
		Assert.NotNull(tokens.AccessToken);

		var introspection = await IntrospectTokenAsync(tokens.AccessToken, "role-claims-client", clientSecret);
		Assert.True(introspection.GetProperty("active").GetBoolean(), "Token should be active");

		Assert.True(introspection.TryGetProperty("role", out var roleClaim),
			"Access token should contain a 'role' claim");

		// The role claim could be a string or an array
		if (roleClaim.ValueKind == JsonValueKind.String)
		{
			Assert.Equal("Admin", roleClaim.GetString());
		}
		else if (roleClaim.ValueKind == JsonValueKind.Array)
		{
			var roles = roleClaim.EnumerateArray().Select(r => r.GetString()).ToList();
			Assert.Contains("Admin", roles);
		}
	}

	[Fact]
	public async Task AccessToken_ContainsCustomUserClaims()
	{
		// Arrange - create a custom scope that allows specific claim types
		var clientSecret = "CustomClaimsSecret123!";
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
		await _factory.SeedOpenIddictScopesAsync();

		// Create a custom scope with UserClaims configured
		var scopeResponse = await _client.PostAsJsonAsync("/api/admin/oauth/scopes",
			new CreateOAuthScopeDto
			{
				Name = "custom-claims",
				DisplayName = "Custom Claims",
				Description = "Scope for custom claims",
				UserClaims = ["department", "employee_id"]
			}, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, scopeResponse.StatusCode);

		// Create confidential client with the custom scope so we can introspect
		var clientResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients",
			new CreateOAuthClientDto
			{
				ClientId = "custom-claims-client",
				DisplayName = "Custom Claims Client",
				ClientType = "confidential",
				ClientSecret = clientSecret,
				ConsentType = "implicit",
				RedirectUris = ["http://localhost/callback"],
				PostLogoutRedirectUris = ["http://localhost"],
				Scopes = ["openid", "profile", "custom-claims"]
			}, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, clientResponse.StatusCode);

		// Logout admin
		await _client.PostAsync("/api/auth/logout", null);

		// Create user with custom claims
		await CreateTestUserWithRolesAndClaimsAsync(
			"customclaimuser", "Test123!@#", "customclaim@test.com",
			customClaims: [("department", "Engineering"), ("employee_id", "EMP-42")]);

		await _client.LoginAsync("customclaimuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"custom-claims-client", "http://localhost/callback", codeChallenge,
			"openid profile custom-claims", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "custom-claims-client", "http://localhost/callback", codeVerifier, clientSecret);

		// Assert - introspect the reference token to check custom claims
		Assert.NotNull(tokens.AccessToken);

		var introspection = await IntrospectTokenAsync(tokens.AccessToken, "custom-claims-client", clientSecret);
		Assert.True(introspection.GetProperty("active").GetBoolean(), "Token should be active");

		Assert.True(introspection.TryGetProperty("department", out var deptClaim),
			"Access token should contain 'department' claim");
		Assert.Equal("Engineering", deptClaim.GetString());

		Assert.True(introspection.TryGetProperty("employee_id", out var empClaim),
			"Access token should contain 'employee_id' claim");
		Assert.Equal("EMP-42", empClaim.GetString());
	}

	[Fact]
	public async Task UserInfo_ReturnsRoleClaims()
	{
		// Arrange
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "userinfo-roles-client",
			DisplayName = "UserInfo Roles Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile", "roles"]
		});

		await CreateTestUserWithRolesAndClaimsAsync(
			"userinforoleuser", "Test123!@#", "userinforole@test.com",
			roles: ["Admin", "Editor"]);

		await _client.LoginAsync("userinforoleuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"userinfo-roles-client", "http://localhost/callback", codeChallenge,
			"openid profile roles", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "userinfo-roles-client", "http://localhost/callback", codeVerifier);

		// Act - call userinfo
		var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
		var response = await _client.SendAsync(request);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(claims.TryGetProperty("role", out var roleClaim),
			"UserInfo should contain 'role' claim when roles scope is requested");

		// Roles should contain both Admin and Editor
		if (roleClaim.ValueKind == JsonValueKind.Array)
		{
			var roles = roleClaim.EnumerateArray().Select(r => r.GetString()).ToList();
			Assert.Contains("Admin", roles);
			Assert.Contains("Editor", roles);
		}
		else
		{
			// Single role case
			Assert.True(roleClaim.GetString() == "Admin" || roleClaim.GetString() == "Editor");
		}
	}

	[Fact]
	public async Task UserInfo_ReturnsCustomClaims()
	{
		// Arrange - create a custom scope and API with UserClaims
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
		await _factory.SeedOpenIddictScopesAsync();

		// Create a custom scope with UserClaims
		var scopeResponse = await _client.PostAsJsonAsync("/api/admin/oauth/scopes",
			new CreateOAuthScopeDto
			{
				Name = "userinfo-custom",
				DisplayName = "UserInfo Custom",
				UserClaims = ["department"]
			}, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, scopeResponse.StatusCode);

		// Create client with the custom scope
		var clientResponse = await _client.PostAsJsonAsync("/api/admin/oauth/clients",
			new CreateOAuthClientDto
			{
				ClientId = "userinfo-custom-client",
				DisplayName = "UserInfo Custom Client",
				ClientType = "public",
				ConsentType = "implicit",
				RedirectUris = ["http://localhost/callback"],
				PostLogoutRedirectUris = ["http://localhost"],
				Scopes = ["openid", "profile", "userinfo-custom"]
			}, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, clientResponse.StatusCode);

		// Logout admin
		await _client.PostAsync("/api/auth/logout", null);

		// Create user with custom claims
		await CreateTestUserWithRolesAndClaimsAsync(
			"userinfocustomuser", "Test123!@#", "userinfocustom@test.com",
			customClaims: [("department", "Sales")]);

		await _client.LoginAsync("userinfocustomuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"userinfo-custom-client", "http://localhost/callback", codeChallenge,
			"openid profile userinfo-custom", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "userinfo-custom-client", "http://localhost/callback", codeVerifier);

		// Act - call userinfo
		var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
		var response = await _client.SendAsync(request);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(claims.TryGetProperty("department", out var deptClaim),
			"UserInfo should contain 'department' claim when the scope allows it");
		Assert.Equal("Sales", deptClaim.GetString());
	}

	[Fact]
	public async Task ClientCredentials_ContainsClientRoles()
	{
		// Arrange - create confidential client with roles
		var clientSecret = "CCRolesSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "cc-roles-client",
			DisplayName = "CC Roles Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid"],
			Roles = ["ServiceAccount", "BatchProcessor"]
		});

		// Act - request token with client credentials
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-roles-client",
			["client_secret"] = clientSecret,
			["scope"] = "openid"
		};

		var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(parameters));

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens?.AccessToken);

		// Introspect the reference token to check role claims
		var introspection = await IntrospectTokenAsync(tokens.AccessToken, "cc-roles-client", clientSecret);
		Assert.True(introspection.GetProperty("active").GetBoolean(), "Token should be active");

		Assert.True(introspection.TryGetProperty("role", out var roleClaim),
			"Client credentials token should contain 'role' claim for client roles");

		if (roleClaim.ValueKind == JsonValueKind.Array)
		{
			var roles = roleClaim.EnumerateArray().Select(r => r.GetString()).ToList();
			Assert.Contains("ServiceAccount", roles);
			Assert.Contains("BatchProcessor", roles);
		}
		else
		{
			// If only one role somehow
			Assert.True(
				roleClaim.GetString() == "ServiceAccount" || roleClaim.GetString() == "BatchProcessor");
		}
	}

	[Fact]
	public async Task ClientCredentials_ContainsClientClaims()
	{
		// Arrange - create confidential client with custom claims
		var clientSecret = "CCClaimsSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "cc-claims-client",
			DisplayName = "CC Claims Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid"],
			Claims =
			[
				new OAuthClientClaimDto { Type = "tenant_id", Value = "tenant-123" },
				new OAuthClientClaimDto { Type = "environment", Value = "production" }
			]
		});

		// Act - request token with client credentials
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-claims-client",
			["client_secret"] = clientSecret,
			["scope"] = "openid"
		};

		var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(parameters));

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens?.AccessToken);

		// Introspect the reference token to check custom claims
		var introspection = await IntrospectTokenAsync(tokens.AccessToken, "cc-claims-client", clientSecret);
		Assert.True(introspection.GetProperty("active").GetBoolean(), "Token should be active");

		Assert.True(introspection.TryGetProperty("tenant_id", out var tenantClaim),
			"Client credentials token should contain 'tenant_id' claim");
		Assert.Equal("tenant-123", tenantClaim.GetString());

		Assert.True(introspection.TryGetProperty("environment", out var envClaim),
			"Client credentials token should contain 'environment' claim");
		Assert.Equal("production", envClaim.GetString());
	}

	#endregion

}
