using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class ReferenceTokenTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public ReferenceTokenTests(SharedPostgresFixture fixture)
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

	private record IntrospectionResponse
	{
		[JsonPropertyName("active")]
		public bool Active { get; init; }

		[JsonPropertyName("sub")]
		public string? Subject { get; init; }

		[JsonPropertyName("client_id")]
		public string? ClientId { get; init; }

		[JsonPropertyName("scope")]
		public string? Scope { get; init; }

		[JsonPropertyName("token_type")]
		public string? TokenType { get; init; }
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

	private static bool IsJwtFormat(string token)
	{
		// A JWT has exactly 3 base64url-encoded parts separated by dots
		var parts = token.Split('.');
		return parts.Length == 3;
	}

	#endregion

	#region Reference Token Tests

	[Fact]
	public async Task AuthorizationCodeFlow_WithReferenceTokenClient_ReturnsOpaqueToken()
	{
		// Arrange - create OAuth client with reference token type
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "ref-token-client",
			DisplayName = "Reference Token Client",
			ClientType = "public",
			ConsentType = "implicit",
			AccessTokenType = AccessTokenType.Reference,
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("refuser", "Test123!@#");
		await _client.LoginAsync("refuser", "Test123!@#", _factory.JsonOptions);

		// Generate PKCE
		var (codeVerifier, codeChallenge) = GeneratePkce();

		// Get authorization code
		var code = await GetAuthorizationCodeAsync(
			"ref-token-client", "http://localhost/callback", codeChallenge,
			"openid profile", "test-state");

		// Exchange code for tokens
		var tokens = await ExchangeCodeForTokensAsync(
			code, "ref-token-client", "http://localhost/callback", codeVerifier);

		// Assert - reference token should be opaque (not JWT format)
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
		Assert.False(IsJwtFormat(tokens.AccessToken),
			"Reference token should be opaque and not in JWT format (should not contain dots)");
	}

	[Fact]
	public async Task ReferenceToken_Introspection_ReturnsActiveTrueWithClaims()
	{
		// Arrange - create confidential client with reference tokens
		var clientSecret = "IntrospectSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "ref-introspect-client",
			DisplayName = "Reference Introspect Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			AccessTokenType = AccessTokenType.Reference,
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("introspectuser", "Test123!@#");
		await _client.LoginAsync("introspectuser", "Test123!@#", _factory.JsonOptions);

		// Get token via auth code flow
		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"ref-introspect-client", "http://localhost/callback", codeChallenge,
			"openid profile", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "ref-introspect-client", "http://localhost/callback", codeVerifier, clientSecret);

		Assert.NotNull(tokens.AccessToken);

		// Act - introspect the token
		var introspectParams = new Dictionary<string, string>
		{
			["token"] = tokens.AccessToken,
			["client_id"] = "ref-introspect-client",
			["client_secret"] = clientSecret
		};

		var introspectResponse = await _client.PostAsync("/system/connect/introspect",
			new FormUrlEncodedContent(introspectParams));

		// Assert
		Assert.Equal(HttpStatusCode.OK, introspectResponse.StatusCode);
		var result = await introspectResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(result.GetProperty("active").GetBoolean(), "Introspection should return active=true");
		Assert.True(result.TryGetProperty("sub", out _), "Introspection should include sub claim");
	}

	[Fact]
	public async Task ReferenceToken_Revocation_MakesTokenInactive()
	{
		// Arrange - create confidential client with reference tokens
		var clientSecret = "RevokeSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "ref-revoke-client",
			DisplayName = "Reference Revoke Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			AccessTokenType = AccessTokenType.Reference,
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("revokeuser", "Test123!@#");
		await _client.LoginAsync("revokeuser", "Test123!@#", _factory.JsonOptions);

		// Get token via auth code flow
		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"ref-revoke-client", "http://localhost/callback", codeChallenge,
			"openid profile", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "ref-revoke-client", "http://localhost/callback", codeVerifier, clientSecret);

		Assert.NotNull(tokens.AccessToken);

		// Act - revoke the token
		var revokeParams = new Dictionary<string, string>
		{
			["token"] = tokens.AccessToken,
			["client_id"] = "ref-revoke-client",
			["client_secret"] = clientSecret
		};

		var revokeResponse = await _client.PostAsync("/system/connect/revoke",
			new FormUrlEncodedContent(revokeParams));

		// Assert - revocation should succeed (200 OK per RFC 7009)
		Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
	}

	[Fact]
	public async Task ReferenceToken_AfterRevocation_IntrospectionReturnsInactive()
	{
		// Arrange - create confidential client with reference tokens
		var clientSecret = "RevokeIntrospectSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "ref-revoke-introspect-client",
			DisplayName = "Reference Revoke+Introspect Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			AccessTokenType = AccessTokenType.Reference,
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("revokeintrospectuser", "Test123!@#");
		await _client.LoginAsync("revokeintrospectuser", "Test123!@#", _factory.JsonOptions);

		// Get token via auth code flow
		var (codeVerifier, codeChallenge) = GeneratePkce();
		var code = await GetAuthorizationCodeAsync(
			"ref-revoke-introspect-client", "http://localhost/callback", codeChallenge,
			"openid profile", "test-state");
		var tokens = await ExchangeCodeForTokensAsync(
			code, "ref-revoke-introspect-client", "http://localhost/callback", codeVerifier, clientSecret);

		Assert.NotNull(tokens.AccessToken);

		// Revoke the token
		var revokeParams = new Dictionary<string, string>
		{
			["token"] = tokens.AccessToken,
			["client_id"] = "ref-revoke-introspect-client",
			["client_secret"] = clientSecret
		};

		await _client.PostAsync("/system/connect/revoke", new FormUrlEncodedContent(revokeParams));

		// Act - introspect the revoked token
		var introspectParams = new Dictionary<string, string>
		{
			["token"] = tokens.AccessToken,
			["client_id"] = "ref-revoke-introspect-client",
			["client_secret"] = clientSecret
		};

		var introspectResponse = await _client.PostAsync("/system/connect/introspect",
			new FormUrlEncodedContent(introspectParams));

		// Assert
		Assert.Equal(HttpStatusCode.OK, introspectResponse.StatusCode);
		var result = await introspectResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(result.GetProperty("active").GetBoolean(),
			"Introspection should return active=false after revocation");
	}

	[Fact]
	public async Task ClientCredentials_WithReferenceToken_ReturnsOpaqueToken()
	{
		// Arrange - create confidential client with reference tokens
		var clientSecret = "CCRefSecret123!";
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "cc-ref-client",
			DisplayName = "CC Reference Token Client",
			ClientType = "confidential",
			ClientSecret = clientSecret,
			ConsentType = "implicit",
			AccessTokenType = AccessTokenType.Reference,
			RedirectUris = ["http://localhost/callback"],
			Scopes = ["openid"]
		});

		// Act - request token with client credentials
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "cc-ref-client",
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
		Assert.False(IsJwtFormat(tokens.AccessToken),
			"Reference token from client_credentials should be opaque and not in JWT format");
	}

	#endregion
}
