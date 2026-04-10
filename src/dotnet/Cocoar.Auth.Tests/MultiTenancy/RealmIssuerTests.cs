using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.MultiTenancy;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.MultiTenancy)]
public class RealmIssuerTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public RealmIssuerTests(SharedPostgresFixture fixture)
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

	private record OpenIdConfiguration
	{
		[JsonPropertyName("issuer")]
		public string? Issuer { get; init; }

		[JsonPropertyName("token_endpoint")]
		public string? TokenEndpoint { get; init; }

		[JsonPropertyName("authorization_endpoint")]
		public string? AuthorizationEndpoint { get; init; }
	}

	private record TokenResponse
	{
		[JsonPropertyName("access_token")]
		public string? AccessToken { get; init; }

		[JsonPropertyName("token_type")]
		public string? TokenType { get; init; }
	}

	private async Task LoginAsAdminAsync()
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
	}

	private async Task CreateRealmAsync(string slug)
	{
		var dto = new CreateRealmDto { Slug = slug, DisplayName = slug };
		var response = await _client.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task Discovery_SystemRealm_ReturnsDomainBasedIssuer()
	{
		// System realm discovery document (Host: system.localhost is default)
		var response = await _client.GetAsync("/.well-known/openid-configuration");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);
		Assert.NotNull(config.Issuer);

		// System realm issuer is domain-based (OpenIddict appends trailing slash)
		Assert.Equal("http://system.localhost/", config.Issuer);
	}

	[Fact]
	public async Task Discovery_RealmScoped_ReturnsDomainBasedIssuer()
	{
		await LoginAsAdminAsync();
		await CreateRealmAsync("issuer-test");

		// Realm-scoped discovery document via Host header
		var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
		request.Headers.Host = "issuer-test.localhost";
		var response = await _client.SendAsync(request);

		var body = await response.Content.ReadAsStringAsync();
		Assert.True(response.StatusCode == HttpStatusCode.OK,
			$"Discovery returned {(int)response.StatusCode}. Body: '{body}'");

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);
		Assert.NotNull(config.Issuer);

		// Realm-scoped issuer should be domain-based (OpenIddict appends trailing slash)
		Assert.Equal("http://issuer-test.localhost/", config.Issuer);
	}

	[Fact]
	public async Task Discovery_RealmScoped_EndpointsAreDomainScoped()
	{
		await LoginAsAdminAsync();
		await CreateRealmAsync("endpoints-test");

		var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
		request.Headers.Host = "endpoints-test.localhost";
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);

		// Token endpoint should be at /connect/token (no realm prefix in path)
		Assert.NotNull(config.TokenEndpoint);
		Assert.Contains("/connect/token", config.TokenEndpoint);
		// Should NOT contain a path-based realm prefix
		Assert.DoesNotContain("/endpoints-test/", config.TokenEndpoint);
	}

	[Fact]
	public async Task ClientCredentials_RealmScoped_Works()
	{
		await LoginAsAdminAsync();
		await _factory.SeedOpenIddictScopesAsync();
		await CreateRealmAsync("cc-realm");

		// Create a confidential client in the realm via Host header
		var createDto = new CreateOAuthClientDto
		{
			ClientId = "realm-cc-client",
			DisplayName = "Realm CC Client",
			ClientType = "confidential",
			ClientSecret = "RealmSecret123!",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid"]
		};

		// Post to the realm-scoped OAuth admin endpoint via Host header
		var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/oauth/clients")
		{
			Content = JsonContent.Create(createDto, options: _factory.JsonOptions)
		};
		createRequest.Headers.Host = "cc-realm.localhost";
		var createResponse = await _client.SendAsync(createRequest);

		if (createResponse.StatusCode == HttpStatusCode.Unauthorized)
		{
			Assert.Fail($"Admin auth did not carry over to realm. Status: {createResponse.StatusCode}");
		}
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// Logout admin
		await _client.PostAsync("/api/auth/logout", null);

		// Request token via realm-scoped endpoint using Host header
		var tokenParams = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "realm-cc-client",
			["client_secret"] = "RealmSecret123!",
			["scope"] = "openid"
		};

		var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
		{
			Content = new FormUrlEncodedContent(tokenParams)
		};
		tokenRequest.Headers.Host = "cc-realm.localhost";
		var tokenResponse = await _client.SendAsync(tokenRequest);

		Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

		var tokens = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens);
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
	}
}
