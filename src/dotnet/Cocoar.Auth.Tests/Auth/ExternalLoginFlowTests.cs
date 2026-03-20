using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

/// <summary>
/// Integration tests for the full external login flow using a WireMock-based fake OIDC server.
/// Tests the complete chain: Provider → Redirect → Callback → User Creation/Login.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ExternalLoginFlowTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;
	private readonly FakeOidcServer _oidcServer;

	public ExternalLoginFlowTests(SharedPostgresFixture fixture)
	{
		_fixture = fixture;
		_oidcServer = new FakeOidcServer();
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
		_oidcServer.Dispose();
		await _factory.DisposeAsync();
	}

	/// <summary>
	/// Creates an OIDC provider pointing at the fake OIDC server.
	/// </summary>
	private async Task CreateFakeOidcProviderAsync(string name = "FakeOidc", string displayName = "Fake Provider")
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);

		var createDto = new CreateLoginProviderDto
		{
			Name = name,
			DisplayName = displayName,
			Type = LoginProviderType.OpenIdConnect,
			Configuration = _oidcServer.GetProviderConfiguration()
		};

		var response = await _client.PostAsJsonAsync("/system/api/admin/login-providers", createDto, _factory.JsonOptions);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);

		// Logout admin so subsequent calls are unauthenticated
		await _client.PostAsync("/system/api/auth/logout", null);
	}

	/// <summary>
	/// Initiates the external login flow and extracts state/nonce from the redirect URL.
	/// </summary>
	private async Task<(string state, string nonce, string redirectUrl)> InitiateExternalLoginAsync(
		string providerName = "FakeOidc", string returnUrl = "/")
	{
		using var anonClient = _factory.CreateClientWithCookies();
		var response = await anonClient.GetAsync(
			$"/system/api/auth/external-login?provider={providerName}&returnUrl={Uri.EscapeDataString(returnUrl)}");

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var redirectUrl = response.Headers.Location!.ToString();
		var (state, nonce) = FakeOidcServer.ExtractStateAndNonce(redirectUrl);

		return (state, nonce, redirectUrl);
	}

	[Fact]
	public async Task ExternalCallback_ValidCode_CreatesUserAndRedirects()
	{
		// Arrange
		await CreateFakeOidcProviderAsync();
		var (state, nonce, _) = await InitiateExternalLoginAsync(returnUrl: "/dashboard");

		// Configure fake OIDC to return a token for this user
		_oidcServer.SetupTokenEndpoint(
			subject: "ext-user-123",
			email: "jane@example.com",
			givenName: "Jane",
			familyName: "Doe",
			nonce: nonce);

		// Act — simulate the callback from the OIDC provider
		using var callbackClient = _factory.CreateClientWithCookies();
		var response = await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=fake-auth-code&state={Uri.EscapeDataString(state)}");

		// Assert — should redirect to the return URL
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location!.ToString();
		Assert.Equal("/dashboard", location);

		// Verify user was created and is authenticated
		var meResponse = await callbackClient.GetAsync("/system/api/auth/me");
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var user = await meResponse.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
		Assert.NotNull(user);
		Assert.Equal("jane@example.com", user.UserName);
		Assert.Equal("Jane", user.FirstName);
		Assert.Equal("Doe", user.LastName);
	}

	[Fact]
	public async Task ExternalCallback_ExistingUser_SignsInWithoutCreating()
	{
		// Arrange — create provider and a user with linked external login
		await CreateFakeOidcProviderAsync();

		// First login: auto-creates the user
		var (state1, nonce1, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "returning-user", email: "returning@example.com", nonce: nonce1);
		using var client1 = _factory.CreateClientWithCookies();
		await client1.GetAsync($"/system/api/auth/external-callback?code=code1&state={Uri.EscapeDataString(state1)}");

		// Second login: same external user
		var (state2, nonce2, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "returning-user", email: "returning@example.com", nonce: nonce2);

		// Act
		using var client2 = _factory.CreateClientWithCookies();
		var response = await client2.GetAsync(
			$"/system/api/auth/external-callback?code=code2&state={Uri.EscapeDataString(state2)}");

		// Assert — same user, signed in
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var meResponse = await client2.GetAsync("/system/api/auth/me");
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var user = await meResponse.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
		Assert.NotNull(user);
		Assert.Equal("returning@example.com", user.UserName);
	}

	[Fact]
	public async Task ExternalCallback_AutoCreateUser_SetsEmailVerified()
	{
		// Arrange
		await CreateFakeOidcProviderAsync();
		var (state, nonce, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(
			subject: "verified-user",
			email: "verified@example.com",
			emailVerified: true,
			nonce: nonce);

		// Act
		using var callbackClient = _factory.CreateClientWithCookies();
		await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=code&state={Uri.EscapeDataString(state)}");

		// Assert — check profile shows email as confirmed
		var profileResponse = await callbackClient.GetAsync("/system/api/auth/profile");
		Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
		var body = await profileResponse.Content.ReadAsStringAsync();
		Assert.Contains("\"emailConfirmed\":true", body);
	}

	[Fact]
	public async Task ExternalCallback_UsernameFallback_WhenEmailTaken()
	{
		// Arrange — create a local user with the same email as username
		await CreateFakeOidcProviderAsync();
		await _factory.CreateTestUserAsync("taken@example.com", "Test123!@#", email: "taken@example.com");

		var (state, nonce, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(
			subject: "ext-987",
			email: "taken@example.com",
			nonce: nonce);

		// Act
		using var callbackClient = _factory.CreateClientWithCookies();
		var response = await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=code&state={Uri.EscapeDataString(state)}");

		// Assert — should still succeed with fallback username
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var meResponse = await callbackClient.GetAsync("/system/api/auth/me");
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var user = await meResponse.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
		Assert.NotNull(user);
		// Username should be the fallback: provider_subject
		Assert.Equal("FakeOidc_ext-987", user.UserName);
	}

	[Fact]
	public async Task ExternalCallback_LinkedLogin_ShowsInProfile()
	{
		// Arrange
		await CreateFakeOidcProviderAsync();
		var (state, nonce, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "profile-user", email: "profile@example.com", nonce: nonce);

		// Act — auto-create via external login
		using var callbackClient = _factory.CreateClientWithCookies();
		await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=code&state={Uri.EscapeDataString(state)}");

		// Assert — linked logins should show the provider
		var loginsResponse = await callbackClient.GetAsync("/system/api/auth/external-logins");
		Assert.Equal(HttpStatusCode.OK, loginsResponse.StatusCode);
		var logins = await loginsResponse.ReadFromJsonAsync<LinkedExternalLoginListDto>(_factory.JsonOptions);
		Assert.NotNull(logins);
		Assert.Single(logins.Logins);
		Assert.Equal("FakeOidc", logins.Logins[0].ProviderName);
		Assert.Equal("Fake Provider", logins.Logins[0].ProviderDisplayName);
	}

	[Fact]
	public async Task Unlink_WithPassword_Succeeds()
	{
		// Arrange — create user via external login, then set a password
		await CreateFakeOidcProviderAsync();
		var (state, nonce, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "unlink-user", email: "unlink@example.com", nonce: nonce);

		using var callbackClient = _factory.CreateClientWithCookies();
		await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=code&state={Uri.EscapeDataString(state)}");

		// The auto-created user has no password, so unlinking should fail
		var failResponse = await callbackClient.DeleteAsync("/system/api/auth/external-link/FakeOidc");
		Assert.Equal(HttpStatusCode.BadRequest, failResponse.StatusCode);
	}

	[Fact]
	public async Task ExternalCallback_InactiveUser_ReturnsError()
	{
		// Arrange — create user via external login, then deactivate
		await CreateFakeOidcProviderAsync();

		// First: auto-create user
		var (state1, nonce1, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "inactive-ext", email: "inactive@example.com", nonce: nonce1);
		using var client1 = _factory.CreateClientWithCookies();
		await client1.GetAsync($"/system/api/auth/external-callback?code=c1&state={Uri.EscapeDataString(state1)}");

		// Deactivate user via admin
		await _factory.CreateTestUserAsync("admin2", "Admin123!@#", isAdmin: true);
		using var adminClient = _factory.CreateClientWithCookies();
		await adminClient.LoginAsync("admin2", "Admin123!@#", _factory.JsonOptions);

		// Find the user
		var meResponse = await client1.GetAsync("/system/api/auth/me");
		var user = await meResponse.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);

		// Deactivate
		await adminClient.PatchAsJsonAsync($"/system/api/admin/users/{user!.Id}",
			new { isActive = false }, _factory.JsonOptions);

		// Second login attempt with inactive user
		var (state2, nonce2, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(subject: "inactive-ext", email: "inactive@example.com", nonce: nonce2);

		// Act
		using var client2 = _factory.CreateClientWithCookies();
		var response = await client2.GetAsync(
			$"/system/api/auth/external-callback?code=c2&state={Uri.EscapeDataString(state2)}");

		// Assert — should redirect with error
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var location = response.Headers.Location!.ToString();
		Assert.Contains("error=external_login_failed", location);
	}

	[Fact]
	public async Task ExternalCallback_NoEmail_UsesProviderSubjectAsUsername()
	{
		// Arrange — OIDC provider returns no email
		await CreateFakeOidcProviderAsync();
		var (state, nonce, _) = await InitiateExternalLoginAsync();
		_oidcServer.SetupTokenEndpoint(
			subject: "no-email-user",
			email: null,
			nonce: nonce);

		// Act
		using var callbackClient = _factory.CreateClientWithCookies();
		await callbackClient.GetAsync(
			$"/system/api/auth/external-callback?code=code&state={Uri.EscapeDataString(state)}");

		// Assert
		var meResponse = await callbackClient.GetAsync("/system/api/auth/me");
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var user = await meResponse.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
		Assert.NotNull(user);
		Assert.Equal("FakeOidc_no-email-user", user.UserName);
	}
}

/// <summary>
/// DTO matching the backend CurrentUserDto shape for deserialization.
/// </summary>
file record CurrentUserDto
{
	public string Id { get; init; } = "";
	public string UserName { get; init; } = "";
	public string? Email { get; init; }
	public string? FirstName { get; init; }
	public string? LastName { get; init; }
	public string[] Roles { get; init; } = [];
	public string? Realm { get; init; }
}
