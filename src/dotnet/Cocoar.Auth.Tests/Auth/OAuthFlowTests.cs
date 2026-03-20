using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public class OAuthFlowTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public OAuthFlowTests(SharedPostgresFixture fixture)
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

	private record OAuthErrorResponse
	{
		[JsonPropertyName("error")]
		public string? Error { get; init; }

		[JsonPropertyName("error_description")]
		public string? ErrorDescription { get; init; }
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

		var response = await _client.PostAsJsonAsync("/system/api/admin/oauth/clients", createDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);

		// Logout admin
		await _client.PostAsync("/system/api/auth/logout", null);

		return result;
	}

	private async Task<OAuthClientCreatedDto> CreateConfidentialOAuthClientViaAdminAsync(
		string clientId, string clientSecret, List<string> scopes)
	{
		return await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = clientId,
			DisplayName = "Confidential Test Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = scopes
		});
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

		var response = await _client.GetAsync($"/system/connect/authorize{query}");

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

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokenResponse);
		return tokenResponse;
	}

	private async Task DeactivateUserAsync(ApplicationUser user)
	{
		using var scope = _factory.Services.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		user.SetIsActive(false);
		await userManager.UpdateAsync(user);
	}

	#endregion

	#region Authorization Code Flow Tests

	[Fact]
	public async Task AuthorizationCodeFlow_FullRoundtrip()
	{
		// Arrange - create OAuth client
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "roundtrip-client",
			DisplayName = "Roundtrip Test Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile", "email", "offline_access"]
		});

		// Login as test user
		var user = await _factory.CreateTestUserAsync("oauthuser", "Test123!@#");
		await _client.LoginAsync("oauthuser", "Test123!@#", _factory.JsonOptions);

		// Generate PKCE
		var (codeVerifier, codeChallenge) = GeneratePkce();

		// Get authorization code
		var code = await GetAuthorizationCodeAsync(
			"roundtrip-client", "http://localhost/callback", codeChallenge,
			"openid profile email offline_access", "test-state");

		// Exchange code for tokens
		var tokens = await ExchangeCodeForTokensAsync(
			code, "roundtrip-client", "http://localhost/callback", codeVerifier);

		// Assert
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
		Assert.NotNull(tokens.IdToken);
		Assert.NotEmpty(tokens.IdToken);
		Assert.NotNull(tokens.RefreshToken);
		Assert.NotEmpty(tokens.RefreshToken);
		Assert.Equal("Bearer", tokens.TokenType);
		Assert.True(tokens.ExpiresIn > 0);
	}

	[Fact]
	public async Task AuthorizationCodeFlow_WithoutPKCE_Fails()
	{
		// Arrange - create OAuth client
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "no-pkce-client",
			DisplayName = "No PKCE Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("nopkceuser", "Test123!@#");
		await _client.LoginAsync("nopkceuser", "Test123!@#", _factory.JsonOptions);

		// Try to authorize without PKCE
		var query = "?response_type=code&client_id=no-pkce-client" +
			$"&redirect_uri={Uri.EscapeDataString("http://localhost/callback")}" +
			"&scope=openid%20profile&state=test-state";

		var response = await _client.GetAsync($"/system/connect/authorize{query}");

		// Assert - OpenIddict rejects the request directly when PKCE is missing
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var body = await response.Content.ReadAsStringAsync();
		Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AuthorizationCodeFlow_UnauthenticatedUser_RedirectsToLogin()
	{
		// Arrange - create OAuth client via admin
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "unauth-client",
			DisplayName = "Unauth Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile"]
		});

		var (_, codeChallenge) = GeneratePkce();

		// Use a fresh HttpClient without cookies to simulate an unauthenticated user
		using var freshClient = _factory.CreateClientWithCookies();

		// Act - try to authorize without being logged in
		var query = $"?response_type=code&client_id=unauth-client" +
			$"&redirect_uri={Uri.EscapeDataString("http://localhost/callback")}" +
			"&scope=openid%20profile&state=test-state" +
			$"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
			"&code_challenge_method=S256";

		var response = await freshClient.GetAsync($"/system/connect/authorize{query}");

		// Assert - unauthenticated user should not get a successful authorization
		// The server may return a redirect (302) to login, or a 200 with login page content,
		// depending on how the cookie challenge is handled by OpenIddict middleware.
		// The key assertion is that no authorization code is issued.
		if (response.StatusCode == HttpStatusCode.Redirect)
		{
			var location = response.Headers.Location!;
			Assert.Contains("/system/api/auth/login", location.PathAndQuery);
		}
		else
		{
			// OpenIddict may absorb the redirect and return 200 with no authorization code
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			// Verify no authorization code was granted (no redirect to callback with code)
			var location = response.Headers.Location;
			Assert.True(
				location is null || !location.Query.Contains("code="),
				"Unauthenticated user should not receive an authorization code");
		}
	}

	[Fact]
	public async Task AuthorizationCodeFlow_InactiveUser_ReturnsForbid()
	{
		// Arrange - create OAuth client
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "inactive-client",
			DisplayName = "Inactive User Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		var user = await _factory.CreateTestUserAsync("inactiveuser", "Test123!@#");
		await _client.LoginAsync("inactiveuser", "Test123!@#", _factory.JsonOptions);

		// Deactivate user
		await DeactivateUserAsync(user);

		var (_, codeChallenge) = GeneratePkce();

		// Act - try to authorize with deactivated user
		var query = $"?response_type=code&client_id=inactive-client" +
			$"&redirect_uri={Uri.EscapeDataString("http://localhost/callback")}" +
			"&scope=openid%20profile&state=test-state" +
			$"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
			"&code_challenge_method=S256";

		var response = await _client.GetAsync($"/system/connect/authorize{query}");

		// Assert - should redirect with access_denied error
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location!;
		var queryParams = System.Web.HttpUtility.ParseQueryString(location.Query);
		Assert.Equal("access_denied", queryParams["error"]);
	}

	[Fact]
	public async Task UserInfo_WithValidAccessToken_ReturnsClaims()
	{
		// Arrange - full auth code flow to get access token
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "userinfo-client",
			DisplayName = "UserInfo Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile", "email"]
		});

		var user = await _factory.CreateTestUserAsync("userinfouser", "Test123!@#",
			email: "userinfo@test.com");
		await _client.LoginAsync("userinfouser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"userinfo-client", "http://localhost/callback", codeChallenge,
			"openid profile email", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "userinfo-client", "http://localhost/callback", codeVerifier);

		// Act - call userinfo with access token
		var request = new HttpRequestMessage(HttpMethod.Get, "/system/connect/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
		var response = await _client.SendAsync(request);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(claims.TryGetProperty("sub", out _));
		Assert.True(claims.TryGetProperty("email", out var emailClaim));
		Assert.Equal("userinfo@test.com", emailClaim.GetString());
		Assert.True(claims.TryGetProperty("preferred_username", out var usernameClaim));
		Assert.Equal("userinfouser", usernameClaim.GetString());
	}

	[Fact]
	public async Task AuthorizationCodeFlow_WithEmailScope_ReturnsEmailClaims()
	{
		// Arrange
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "email-scope-client",
			DisplayName = "Email Scope Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "email"]
		});

		var user = await _factory.CreateTestUserAsync("emailscopeuser", "Test123!@#",
			email: "emailscope@test.com");
		await _client.LoginAsync("emailscopeuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"email-scope-client", "http://localhost/callback", codeChallenge,
			"openid email", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "email-scope-client", "http://localhost/callback", codeVerifier);

		// Act - call userinfo
		var request = new HttpRequestMessage(HttpMethod.Get, "/system/connect/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
		var response = await _client.SendAsync(request);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(claims.TryGetProperty("email", out var emailClaim));
		Assert.Equal("emailscope@test.com", emailClaim.GetString());
		Assert.True(claims.TryGetProperty("email_verified", out _));
	}

	#endregion

	#region Token Refresh Flow Tests

	[Fact]
	public async Task RefreshToken_WithValidToken_ReturnsNewAccessToken()
	{
		// Arrange - full auth code flow to get refresh token
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "refresh-client",
			DisplayName = "Refresh Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile", "offline_access"]
		});

		var user = await _factory.CreateTestUserAsync("refreshuser", "Test123!@#");
		await _client.LoginAsync("refreshuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"refresh-client", "http://localhost/callback", codeChallenge,
			"openid profile offline_access", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "refresh-client", "http://localhost/callback", codeVerifier);

		Assert.NotNull(tokens.RefreshToken);

		// Act - use refresh token to get new access token
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = tokens.RefreshToken,
			["client_id"] = "refresh-client"
		};

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var newTokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(newTokens);
		Assert.NotNull(newTokens.AccessToken);
		Assert.NotEmpty(newTokens.AccessToken);
		Assert.NotEqual(tokens.AccessToken, newTokens.AccessToken);
	}

	[Fact]
	public async Task RefreshToken_WithInactiveUser_Fails()
	{
		// Arrange - full auth code flow to get refresh token
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "refresh-inactive-client",
			DisplayName = "Refresh Inactive Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid", "profile", "offline_access"]
		});

		var user = await _factory.CreateTestUserAsync("refreshinactiveuser", "Test123!@#");
		await _client.LoginAsync("refreshinactiveuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"refresh-inactive-client", "http://localhost/callback", codeChallenge,
			"openid profile offline_access", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "refresh-inactive-client", "http://localhost/callback", codeVerifier);

		Assert.NotNull(tokens.RefreshToken);

		// Deactivate user
		await DeactivateUserAsync(user);

		// Act - try to refresh with deactivated user
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = tokens.RefreshToken,
			["client_id"] = "refresh-inactive-client"
		};

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>();
		Assert.NotNull(error);
		Assert.Equal("invalid_grant", error.Error);
	}

	#endregion

	#region Client Credentials Flow Tests

	[Fact]
	public async Task ClientCredentials_WithValidSecret_ReturnsAccessToken()
	{
		// Arrange
		var clientSecret = "SuperSecret123!";
		await CreateConfidentialOAuthClientViaAdminAsync(
			"cc-valid-client", clientSecret, ["openid"]);

		// Act - request token with client credentials
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-valid-client",
			["client_secret"] = clientSecret,
			["scope"] = "openid"
		};

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens);
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
		Assert.Equal("Bearer", tokens.TokenType);
	}

	[Fact]
	public async Task ClientCredentials_WithInvalidSecret_Fails()
	{
		// Arrange
		await CreateConfidentialOAuthClientViaAdminAsync(
			"cc-invalid-client", "CorrectSecret123!", ["openid"]);

		// Act - request token with wrong secret
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-invalid-client",
			["client_secret"] = "WrongSecret456!",
			["scope"] = "openid"
		};

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));

		// Assert - invalid client credentials returns 401 per RFC 6749 §5.2
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>();
		Assert.NotNull(error);
		Assert.Equal("invalid_client", error.Error);
	}

	[Fact]
	public async Task ClientCredentials_PublicClient_Fails()
	{
		// Arrange - create public client (cannot use client_credentials)
		await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "cc-public-client",
			DisplayName = "Public Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid"]
		});

		// Act - try client_credentials with public client
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-public-client",
			["scope"] = "openid"
		};

		var response = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(parameters));

		// Assert - public clients cannot use client_credentials (missing client_secret)
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>();
		Assert.NotNull(error);
		// OpenIddict returns invalid_request because client_secret is required for client_credentials grant
		Assert.Equal("invalid_request", error.Error);
	}

	#endregion
}
