using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.MultiTenancy;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.MultiTenancy)]
public class RealmIsolationTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _systemAdmin = null!;

	public RealmIsolationTests(SharedPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		var connectionString = await _fixture.CreateIsolatedDatabasesAsync();
		_factory = new CocoarAuthWebApplicationFactory(connectionString);
		_systemAdmin = _factory.CreateClientWithCookies();
	}

	public async Task DisposeAsync()
	{
		_systemAdmin.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task LoginAsSystemAdminAsync()
	{
		await _factory.CreateTestUserAsync("sysadmin", "Admin123!@#", isAdmin: true);
		await _systemAdmin.LoginAsync("sysadmin", "Admin123!@#", _factory.JsonOptions);
	}

	/// <summary>
	/// Helper to send a request with a specific Host header for realm-scoped operations.
	/// </summary>
	private static HttpRequestMessage RealmRequest(HttpMethod method, string path, string realmSlug, HttpContent? content = null)
	{
		var request = new HttpRequestMessage(method, path) { Content = content };
		request.Headers.Host = $"{realmSlug}.localhost";
		return request;
	}

	[Fact]
	public async Task Users_InRealmA_NotVisibleInRealmB()
	{
		await LoginAsSystemAdminAsync();

		// Create two realms with admins
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "realm-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "realm-b", "admin-b", "AdminB123!@#");

		// Create an extra user in realm A
		var realmAClient = _factory.CreateClientWithCookies().WithHost("realm-a.localhost");
		await realmAClient.LoginInRealmAsync("realm-a", "admin-a", "AdminA123!@#", _factory.JsonOptions);

		var createUserDto = new CreateUserDto
		{
			UserName = "realm-a-only-user",
			Password = "User123!@#",
			Email = "onlya@test.com"
		};
		var createResponse = await realmAClient.PostAsJsonAsync(
			"/api/admin/users", createUserDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// List users in realm B — should NOT contain realm-a-only-user
		var realmBClient = _factory.CreateClientWithCookies().WithHost("realm-b.localhost");
		await realmBClient.LoginInRealmAsync("realm-b", "admin-b", "AdminB123!@#", _factory.JsonOptions);

		var listResponse = await realmBClient.GetAsync("/api/admin/users");
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

		var users = await listResponse.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
		Assert.NotNull(users);
		Assert.DoesNotContain(users.Items, u => u.UserName == "realm-a-only-user");

		realmAClient.Dispose();
		realmBClient.Dispose();
	}

	[Fact]
	public async Task Roles_InRealmA_NotVisibleInRealmB()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "role-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "role-b", "admin-b", "AdminB123!@#");

		// Create a custom role in realm A
		var realmAClient = _factory.CreateClientWithCookies().WithHost("role-a.localhost");
		await realmAClient.LoginInRealmAsync("role-a", "admin-a", "AdminA123!@#", _factory.JsonOptions);

		var createRoleDto = new CreateRoleDto { Name = "RealmASpecialRole", Description = "Only in A" };
		var createResponse = await realmAClient.PostAsJsonAsync(
			"/api/admin/roles", createRoleDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// List roles in realm B — should NOT contain RealmASpecialRole
		var realmBClient = _factory.CreateClientWithCookies().WithHost("role-b.localhost");
		await realmBClient.LoginInRealmAsync("role-b", "admin-b", "AdminB123!@#", _factory.JsonOptions);

		var listResponse = await realmBClient.GetAsync("/api/admin/roles");
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

		var roles = await listResponse.ReadFromJsonAsync<RoleListDto>(_factory.JsonOptions);
		Assert.NotNull(roles);
		Assert.DoesNotContain(roles.Items, r => r.Name == "RealmASpecialRole");

		realmAClient.Dispose();
		realmBClient.Dispose();
	}

	[Fact]
	public async Task OAuthClients_InRealmA_NotVisibleInRealmB()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "oauth-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "oauth-b", "admin-b", "AdminB123!@#");

		// Create an OAuth client in realm A using Host header
		var createClientDto = new CreateOAuthClientDto
		{
			ClientId = "realm-a-client",
			DisplayName = "Realm A Client",
			ClientType = "confidential",
			ClientSecret = "Secret123!@#",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid"]
		};
		var createRequest = RealmRequest(HttpMethod.Post, "/api/admin/oauth/clients", "oauth-a",
			JsonContent.Create(createClientDto, options: _factory.JsonOptions));
		var createResponse = await _systemAdmin.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// List clients in realm B — should NOT contain realm-a-client
		var listRequest = RealmRequest(HttpMethod.Get, "/api/admin/oauth/clients", "oauth-b");
		var listResponse = await _systemAdmin.SendAsync(listRequest);
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

		var clients = await listResponse.ReadFromJsonAsync<OAuthClientListDto>(_factory.JsonOptions);
		Assert.NotNull(clients);
		Assert.DoesNotContain(clients.Items, c => c.ClientId == "realm-a-client");
	}

	[Fact]
	public async Task LoginProviders_InRealmA_NotVisibleInRealmB()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "lp-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "lp-b", "admin-b", "AdminB123!@#");

		// Create an OIDC login provider in realm A using Host header
		var createProviderDto = new CreateLoginProviderDto
		{
			Name = "realm-a-oidc",
			DisplayName = "Realm A OIDC",
			Type = LoginProviderType.OpenIdConnect,
			Configuration = new Dictionary<string, string>
			{
				["Authority"] = "https://example.com",
				["ClientId"] = "test-client"
			}
		};
		var createRequest = RealmRequest(HttpMethod.Post, "/api/admin/login-providers", "lp-a",
			JsonContent.Create(createProviderDto, options: _factory.JsonOptions));
		var createResponse = await _systemAdmin.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// List providers in realm B — should NOT contain realm-a-oidc
		var listRequest = RealmRequest(HttpMethod.Get, "/api/admin/login-providers", "lp-b");
		var listResponse = await _systemAdmin.SendAsync(listRequest);
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

		var providers = await listResponse.ReadFromJsonAsync<LoginProviderListDto>(_factory.JsonOptions);
		Assert.NotNull(providers);
		Assert.DoesNotContain(providers.Items, p => p.Name == "realm-a-oidc");
	}

	[Fact]
	public async Task SystemAdmin_CanAccessRealmAdminEndpoints()
	{
		await LoginAsSystemAdminAsync();

		// Create a realm
		var dto = new CreateRealmDto { Slug = "access-test", DisplayName = "Access Test" };
		var createResponse = await _systemAdmin.PostAsJsonAsync("/api/admin/realms", dto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// System admin can access the realm's admin endpoints via Host header
		var usersRequest = RealmRequest(HttpMethod.Get, "/api/admin/users", "access-test");
		var usersResponse = await _systemAdmin.SendAsync(usersRequest);
		Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);

		var rolesRequest = RealmRequest(HttpMethod.Get, "/api/admin/roles", "access-test");
		var rolesResponse = await _systemAdmin.SendAsync(rolesRequest);
		Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
	}

	[Fact]
	public async Task RealmSetup_CreatesAdminInCorrectRealm()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "setup-iso", "realm-admin", "RealmAdmin123!@#");

		// The admin should exist in setup-iso
		var realmUsersRequest = RealmRequest(HttpMethod.Get, "/api/admin/users", "setup-iso");
		var realmUsers = await _systemAdmin.SendAsync(realmUsersRequest);
		Assert.Equal(HttpStatusCode.OK, realmUsers.StatusCode);
		var users = await realmUsers.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
		Assert.NotNull(users);
		Assert.Contains(users.Items, u => u.UserName == "realm-admin");

		// The admin should NOT exist in the system realm's user list
		var systemUsers = await _systemAdmin.GetAsync("/api/admin/users");
		Assert.Equal(HttpStatusCode.OK, systemUsers.StatusCode);
		var sysUsers = await systemUsers.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
		Assert.NotNull(sysUsers);
		Assert.DoesNotContain(sysUsers.Items, u => u.UserName == "realm-admin");
	}

	[Fact]
	public async Task RealmAdmin_CookieIsolatedPerDomain()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "cookie-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "cookie-b", "admin-b", "AdminB123!@#");

		// Login as realm A admin
		var realmAClient = _factory.CreateClientWithCookies().WithHost("cookie-a.localhost");
		var loginResponse = await realmAClient.LoginInRealmAsync(
			"cookie-a", "admin-a", "AdminA123!@#", _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

		// Realm A admin can access realm A endpoints
		var ownRealmResponse = await realmAClient.GetAsync("/api/admin/users");
		Assert.Equal(HttpStatusCode.OK, ownRealmResponse.StatusCode);

		// A separate client (no realm A cookie) targeting realm B should be unauthorized.
		// In production, the browser isolates cookies per domain automatically.
		// In tests, we simulate this by using a fresh client without the realm A session.
		var realmBClient = _factory.CreateClientWithCookies().WithHost("cookie-b.localhost");
		var otherRealmResponse = await realmBClient.GetAsync("/api/admin/users");
		Assert.Equal(HttpStatusCode.Unauthorized, otherRealmResponse.StatusCode);

		realmAClient.Dispose();
		realmBClient.Dispose();
	}

	[Fact]
	public async Task OAuthToken_IsolatedPerRealm()
	{
		await LoginAsSystemAdminAsync();

		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "token-a", "admin-a", "AdminA123!@#");
		await _factory.CreateRealmWithAdminAsync(_systemAdmin, "token-b", "admin-b", "AdminB123!@#");

		// Create an OAuth client in realm A using Host header
		var createClientDto = new CreateOAuthClientDto
		{
			ClientId = "isolated-client",
			DisplayName = "Isolated Client",
			ClientType = "confidential",
			ClientSecret = "IsoSecret123!",
			ConsentType = "implicit",
			RedirectUris = ["http://localhost/callback"],
			PostLogoutRedirectUris = ["http://localhost"],
			Scopes = ["openid"]
		};
		var createRequest = RealmRequest(HttpMethod.Post, "/api/admin/oauth/clients", "token-a",
			JsonContent.Create(createClientDto, options: _factory.JsonOptions));
		var createResponse = await _systemAdmin.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		// Get a token from realm A
		var tokenParams = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = "isolated-client",
			["client_secret"] = "IsoSecret123!",
			["scope"] = "openid"
		};
		var tokenRequest = RealmRequest(HttpMethod.Post, "/connect/token", "token-a",
			new FormUrlEncodedContent(tokenParams));
		var tokenResponse = await _systemAdmin.SendAsync(tokenRequest);
		Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

		var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(token?.AccessToken);

		// Same client_id does NOT exist in realm B — token request should fail
		var realmBTokenRequest = RealmRequest(HttpMethod.Post, "/connect/token", "token-b",
			new FormUrlEncodedContent(tokenParams));
		var realmBTokenResponse = await _systemAdmin.SendAsync(realmBTokenRequest);
		// Should fail — client doesn't exist in realm B
		Assert.NotEqual(HttpStatusCode.OK, realmBTokenResponse.StatusCode);
	}

	private record TokenResponse
	{
		[JsonPropertyName("access_token")]
		public string? AccessToken { get; init; }
	}
}
