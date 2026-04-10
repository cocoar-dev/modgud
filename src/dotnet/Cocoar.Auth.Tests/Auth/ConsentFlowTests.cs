using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.OAuth)]
public class ConsentFlowTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public ConsentFlowTests(SharedPostgresFixture fixture)
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

	private record ConsentModel
	{
		[JsonPropertyName("clientId")]
		public string? ClientId { get; init; }

		[JsonPropertyName("clientName")]
		public string? ClientName { get; init; }

		[JsonPropertyName("requestedScopes")]
		public List<ConsentScopeInfo>? RequestedScopes { get; init; }

		[JsonPropertyName("returnUrl")]
		public string? ReturnUrl { get; init; }
	}

	private record ConsentScopeInfo
	{
		[JsonPropertyName("name")]
		public string? Name { get; init; }

		[JsonPropertyName("displayName")]
		public string? DisplayName { get; init; }

		[JsonPropertyName("description")]
		public string? Description { get; init; }

		[JsonPropertyName("required")]
		public bool Required { get; init; }
	}

	private record ConsentResult
	{
		[JsonPropertyName("redirectUrl")]
		public string? RedirectUrl { get; init; }
	}

	#endregion

	#region Helper Methods

	/// <summary>
	/// Ensures a URI is absolute. If the URI is relative, it is combined with a default base URI.
	/// This is needed because redirect Location headers from the test server may be relative.
	/// </summary>
	private static Uri EnsureAbsoluteUri(Uri uri)
	{
		if (uri.IsAbsoluteUri)
			return uri;
		return new Uri(new Uri("http://localhost"), uri.OriginalString);
	}

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

	private string BuildAuthorizeQuery(string clientId, string codeChallenge, string scope, string state)
	{
		return $"?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
			$"&redirect_uri={Uri.EscapeDataString("http://localhost/callback")}" +
			$"&scope={Uri.EscapeDataString(scope)}" +
			$"&state={Uri.EscapeDataString(state)}" +
			$"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
			$"&code_challenge_method=S256";
	}

	private async Task<TokenResponse> ExchangeCodeForTokensAsync(
		string code, string clientId, string redirectUri, string codeVerifier)
	{
		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["client_id"] = clientId,
			["redirect_uri"] = redirectUri,
			["code_verifier"] = codeVerifier
		};

		var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(parameters));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokenResponse);
		return tokenResponse;
	}

	#endregion

	#region Consent Flow Tests

	[Fact]
	public async Task Authorize_WithExplicitConsent_RedirectsToConsentPage()
	{
		// Arrange - create client with explicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "explicit-consent-client",
			DisplayName = "Explicit Consent Client",
			ClientType = "public",
			ConsentType = "explicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile", "email"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("consentuser", "Test123!@#");
		await _client.LoginAsync("consentuser", "Test123!@#", _factory.JsonOptions);

		var (_, codeChallenge) = GeneratePkce();
		var query = BuildAuthorizeQuery("explicit-consent-client", codeChallenge, "openid profile email", "test-state");

		// Act - start authorization flow
		var response = await _client.GetAsync($"/connect/authorize{query}");

		// Assert - should redirect to consent page
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = EnsureAbsoluteUri(response.Headers.Location!);
		Assert.StartsWith("/consent", location.AbsolutePath);
		Assert.Contains("returnUrl=", location.Query);
	}

	[Fact]
	public async Task ConsentApi_GetModel_ReturnsRequestedScopes()
	{
		// Arrange - create client with explicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "consent-model-client",
			DisplayName = "Consent Model Client",
			ClientType = "public",
			ConsentType = "explicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile", "email"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("consentmodeluser", "Test123!@#");
		await _client.LoginAsync("consentmodeluser", "Test123!@#", _factory.JsonOptions);

		var (_, codeChallenge) = GeneratePkce();
		var query = BuildAuthorizeQuery("consent-model-client", codeChallenge, "openid profile email", "test-state");

		// Start auth flow to get redirect to consent
		var authResponse = await _client.GetAsync($"/connect/authorize{query}");
		Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
		var consentLocation = EnsureAbsoluteUri(authResponse.Headers.Location!);

		// Extract the returnUrl from the consent redirect
		var consentQuery = System.Web.HttpUtility.ParseQueryString(consentLocation.Query);
		var returnUrl = consentQuery["returnUrl"];
		Assert.NotNull(returnUrl);

		// Act - call the consent API to get the model
		var consentApiUrl = $"/api/consent?returnUrl={Uri.EscapeDataString(returnUrl)}";
		var consentResponse = await _client.GetAsync(consentApiUrl);

		// Assert
		Assert.Equal(HttpStatusCode.OK, consentResponse.StatusCode);
		var model = await consentResponse.Content.ReadFromJsonAsync<ConsentModel>();
		Assert.NotNull(model);
		Assert.Equal("consent-model-client", model.ClientId);
		Assert.Equal("Consent Model Client", model.ClientName);
		Assert.NotNull(model.RequestedScopes);
		Assert.True(model.RequestedScopes.Count >= 3);

		var scopeNames = model.RequestedScopes.Select(s => s.Name).ToList();
		Assert.Contains("openid", scopeNames);
		Assert.Contains("profile", scopeNames);
		Assert.Contains("email", scopeNames);

		// openid should be required
		var openIdScope = model.RequestedScopes.First(s => s.Name == "openid");
		Assert.True(openIdScope.Required, "openid scope should be marked as required");
	}

	[Fact]
	public async Task ConsentApi_Approve_CompletesAuthorizationFlow()
	{
		// Arrange - create client with explicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "consent-approve-client",
			DisplayName = "Consent Approve Client",
			ClientType = "public",
			ConsentType = "explicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("consentapproveuser", "Test123!@#");
		await _client.LoginAsync("consentapproveuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier, codeChallenge) = GeneratePkce();
		var query = BuildAuthorizeQuery("consent-approve-client", codeChallenge, "openid profile", "test-state");

		// Start auth flow to get redirect to consent
		var authResponse = await _client.GetAsync($"/connect/authorize{query}");
		Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
		var consentLocation = EnsureAbsoluteUri(authResponse.Headers.Location!);

		// Extract the returnUrl from the consent redirect
		var consentQuery = System.Web.HttpUtility.ParseQueryString(consentLocation.Query);
		var returnUrl = consentQuery["returnUrl"];
		Assert.NotNull(returnUrl);

		// Act - approve consent
		var consentDecision = new
		{
			approved = true,
			approvedScopes = new[] { "openid", "profile" },
			returnUrl = returnUrl
		};

		var submitResponse = await _client.PostAsJsonAsync("/api/consent", consentDecision);
		Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

		var consentResult = await submitResponse.Content.ReadFromJsonAsync<ConsentResult>();
		Assert.NotNull(consentResult);
		Assert.NotNull(consentResult.RedirectUrl);

		// Now re-hit the authorize endpoint - it should find the permanent authorization and issue the code
		var retryResponse = await _client.GetAsync(consentResult.RedirectUrl);
		Assert.Equal(HttpStatusCode.Redirect, retryResponse.StatusCode);

		var redirectLocation = retryResponse.Headers.Location!;
		var redirectParams = System.Web.HttpUtility.ParseQueryString(redirectLocation.Query);
		var code = redirectParams["code"];
		Assert.NotNull(code);

		// Exchange code for tokens
		var tokens = await ExchangeCodeForTokensAsync(
			code, "consent-approve-client", "http://localhost/callback", codeVerifier);

		// Assert - we successfully got tokens after consent approval
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
	}

	[Fact]
	public async Task ConsentApi_Deny_ReturnsError()
	{
		// Arrange - create client with explicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "consent-deny-client",
			DisplayName = "Consent Deny Client",
			ClientType = "public",
			ConsentType = "explicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("consentdenyuser", "Test123!@#");
		await _client.LoginAsync("consentdenyuser", "Test123!@#", _factory.JsonOptions);

		var (_, codeChallenge) = GeneratePkce();
		var query = BuildAuthorizeQuery("consent-deny-client", codeChallenge, "openid profile", "test-state");

		// Start auth flow to get redirect to consent
		var authResponse = await _client.GetAsync($"/connect/authorize{query}");
		Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
		var consentLocation = EnsureAbsoluteUri(authResponse.Headers.Location!);

		// Extract the returnUrl
		var consentQuery = System.Web.HttpUtility.ParseQueryString(consentLocation.Query);
		var returnUrl = consentQuery["returnUrl"];
		Assert.NotNull(returnUrl);

		// Act - deny consent
		var consentDecision = new
		{
			approved = false,
			approvedScopes = Array.Empty<string>(),
			returnUrl = returnUrl
		};

		var submitResponse = await _client.PostAsJsonAsync("/api/consent", consentDecision);

		// Assert
		Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);
		var consentResult = await submitResponse.Content.ReadFromJsonAsync<ConsentResult>();
		Assert.NotNull(consentResult);
		Assert.NotNull(consentResult.RedirectUrl);
		// The redirect URL should contain an error indicating denial
		Assert.Contains("denied", consentResult.RedirectUrl, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Authorize_WithImplicitConsent_SkipsConsentPage()
	{
		// Arrange - create client with implicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "implicit-consent-client",
			DisplayName = "Implicit Consent Client",
			ClientType = "public",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("implicitconsentuser", "Test123!@#");
		await _client.LoginAsync("implicitconsentuser", "Test123!@#", _factory.JsonOptions);

		var (_, codeChallenge) = GeneratePkce();
		var query = BuildAuthorizeQuery("implicit-consent-client", codeChallenge, "openid profile", "test-state");

		// Act - start authorization flow
		var response = await _client.GetAsync($"/connect/authorize{query}");

		// Assert - should redirect directly to callback with code (skipping consent)
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location!;
		// Should redirect to the callback URI, not to /consent
		Assert.StartsWith("http://localhost/callback", location.GetLeftPart(UriPartial.Path));
		var queryParams = System.Web.HttpUtility.ParseQueryString(location.Query);
		Assert.NotNull(queryParams["code"]);
	}

	[Fact]
	public async Task Authorize_WithPriorConsent_SkipsConsentPage()
	{
		// Arrange - create client with explicit consent
		var created = await CreateOAuthClientViaAdminAsync(new CreateOAuthClientDto
		{
			ClientId = "prior-consent-client",
			DisplayName = "Prior Consent Client",
			ClientType = "public",
			ConsentType = "explicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid", "profile"]
		});

		// Login as test user
		await _factory.CreateTestUserAsync("priorconsentuser", "Test123!@#");
		await _client.LoginAsync("priorconsentuser", "Test123!@#", _factory.JsonOptions);

		var (codeVerifier1, codeChallenge1) = GeneratePkce();
		var query1 = BuildAuthorizeQuery("prior-consent-client", codeChallenge1, "openid profile", "test-state-1");

		// First authorization request - should redirect to consent
		var firstAuthResponse = await _client.GetAsync($"/connect/authorize{query1}");
		Assert.Equal(HttpStatusCode.Redirect, firstAuthResponse.StatusCode);
		var consentLocation = EnsureAbsoluteUri(firstAuthResponse.Headers.Location!);
		Assert.StartsWith("/consent", consentLocation.AbsolutePath);

		// Extract returnUrl and approve consent
		var consentQuery = System.Web.HttpUtility.ParseQueryString(consentLocation.Query);
		var returnUrl = consentQuery["returnUrl"];
		Assert.NotNull(returnUrl);

		var consentDecision = new
		{
			approved = true,
			approvedScopes = new[] { "openid", "profile" },
			returnUrl = returnUrl
		};
		await _client.PostAsJsonAsync("/api/consent", consentDecision);

		// Act - second authorization request with same scopes should skip consent
		var (_, codeChallenge2) = GeneratePkce();
		var query2 = BuildAuthorizeQuery("prior-consent-client", codeChallenge2, "openid profile", "test-state-2");

		var secondAuthResponse = await _client.GetAsync($"/connect/authorize{query2}");

		// Assert - should redirect directly to callback (prior consent found)
		Assert.Equal(HttpStatusCode.Redirect, secondAuthResponse.StatusCode);
		var secondLocation = secondAuthResponse.Headers.Location!;
		Assert.StartsWith("http://localhost/callback", secondLocation.GetLeftPart(UriPartial.Path));
		var secondParams = System.Web.HttpUtility.ParseQueryString(secondLocation.Query);
		Assert.NotNull(secondParams["code"]);
	}

	#endregion
}
