using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.MultiTenancy;

[Collection(IntegrationTestCollection.Name)]
public class RealmIssuerTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public RealmIssuerTests(SharedPostgresFixture fixture)
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
		var response = await _client.PostAsJsonAsync("/system/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task Discovery_SystemRealm_ReturnsBaseIssuer()
	{
		// System realm discovery document
		var response = await _client.GetAsync("/system/.well-known/openid-configuration");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);
		Assert.NotNull(config.Issuer);

		// System realm issuer includes /system PathBase
		Assert.Equal("http://localhost/system", config.Issuer);
	}

	[Fact]
	public async Task Discovery_RealmScoped_ReturnsRealmIssuer()
	{
		await LoginAsAdminAsync();
		await CreateRealmAsync("issuer-test");

		// Realm-scoped discovery document
		// Try both with and without trailing content to debug routing
		var response = await _client.GetAsync("/issuer-test/.well-known/openid-configuration");

		// If 404, the OpenIddict endpoint router doesn't see the rewritten path
		var body = await response.Content.ReadAsStringAsync();
		Assert.True(response.StatusCode == HttpStatusCode.OK,
			$"Discovery returned {(int)response.StatusCode}. Body: '{body}'");

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);
		Assert.NotNull(config.Issuer);

		// Realm-scoped issuer should include the realm path
		Assert.StartsWith("http://localhost/issuer-test", config.Issuer);
	}

	[Fact]
	public async Task Discovery_RealmScoped_EndpointsAreRealmScoped()
	{
		await LoginAsAdminAsync();
		await CreateRealmAsync("endpoints-test");

		var response = await _client.GetAsync("/endpoints-test/.well-known/openid-configuration");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var config = await response.Content.ReadFromJsonAsync<OpenIdConfiguration>();
		Assert.NotNull(config);

		// Token endpoint should be realm-scoped
		Assert.NotNull(config.TokenEndpoint);
		Assert.Contains("/endpoints-test/", config.TokenEndpoint);
	}

	[Fact]
	public async Task ClientCredentials_RealmScoped_Works()
	{
		await LoginAsAdminAsync();
		await _factory.SeedOpenIddictScopesAsync();
		await CreateRealmAsync("cc-realm");

		// Create an OAuth client in the new realm (via system admin, then register in realm)
		// We need to seed scopes in the realm first (done by CreateRealmAsync)
		// Then create a client in the realm by posting to the realm-scoped admin endpoint
		// But RealmsAdminController is SystemRealmOnly, so we create the client via the system realm
		// and the realm's own OAuthAdmin endpoint

		// First seed scopes in the realm (already done by CreateRealmAsync provisioning)

		// Create a confidential client in the realm
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

		// Post to the realm-scoped OAuth admin endpoint
		var createResponse = await _client.PostAsJsonAsync(
			"/cc-realm/api/admin/oauth/clients", createDto, _factory.JsonOptions);

		// If admin auth doesn't carry over to realm (cookie path scoping),
		// we may get 401. In that case, the system realm admin endpoint works.
		if (createResponse.StatusCode == HttpStatusCode.Unauthorized)
		{
			// Admin cookie is for system realm (path "/"), should work for all paths
			// If not, create via system and this test needs adjustment
			Assert.Fail($"Admin auth did not carry over to realm. Status: {createResponse.StatusCode}");
		}
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// Logout admin
		await _client.PostAsync("/system/api/auth/logout", null);

		// Request token via realm-scoped endpoint
		var tokenParams = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "realm-cc-client",
			["client_secret"] = "RealmSecret123!",
			["scope"] = "openid"
		};

		var tokenResponse = await _client.PostAsync(
			"/cc-realm/connect/token",
			new FormUrlEncodedContent(tokenParams));

		Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

		var tokens = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens);
		Assert.NotNull(tokens.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
	}
}
