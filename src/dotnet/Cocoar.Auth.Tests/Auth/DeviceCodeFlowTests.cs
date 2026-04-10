using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.OAuth)]
public class DeviceCodeFlowTests : IAsyncLifetime
{
	private readonly SharedPostgresFixture _fixture;
	private CocoarAuthWebApplicationFactory _factory = null!;
	private HttpClient _client = null!;

	public DeviceCodeFlowTests(SharedPostgresFixture fixture)
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

	private record DeviceAuthorizationResponse
	{
		[JsonPropertyName("device_code")]
		public string? DeviceCode { get; init; }

		[JsonPropertyName("user_code")]
		public string? UserCode { get; init; }

		[JsonPropertyName("verification_uri")]
		public string? VerificationUri { get; init; }

		[JsonPropertyName("verification_uri_complete")]
		public string? VerificationUriComplete { get; init; }

		[JsonPropertyName("expires_in")]
		public int ExpiresIn { get; init; }

		[JsonPropertyName("interval")]
		public int Interval { get; init; }
	}

	private record TokenResponse
	{
		[JsonPropertyName("access_token")]
		public string? AccessToken { get; init; }

		[JsonPropertyName("token_type")]
		public string? TokenType { get; init; }

		[JsonPropertyName("expires_in")]
		public int ExpiresIn { get; init; }

		[JsonPropertyName("error")]
		public string? Error { get; init; }
	}

	private async Task<OAuthClientCreatedDto> CreateDeviceCodeClientAsync()
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
		await _factory.SeedOpenIddictScopesAsync();

		var response = await _client.PostAsJsonAsync("/api/admin/oauth/clients", new CreateOAuthClientDto
		{
			ClientId = "device-client",
			DisplayName = "Device Test Client",
			ClientType = "public",
			ConsentType = "implicit",
			AllowedGrantTypes = ["urn:ietf:params:oauth:grant-type:device_code"],
			Scopes = ["openid", "profile"]
		}, _factory.JsonOptions);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<OAuthClientCreatedDto>(_factory.JsonOptions);
		Assert.NotNull(result);

		await _client.PostAsync("/api/auth/logout", null);
		return result;
	}

	[Fact]
	public async Task DeviceAuthorization_ReturnsCodesAndVerificationUri()
	{
		// Arrange
		await CreateDeviceCodeClientAsync();

		// Act - request device code
		var response = await _client.PostAsync("/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["client_id"] = "device-client",
			["scope"] = "openid profile"
		}));

		// Assert
		if (response.StatusCode != HttpStatusCode.OK)
		{
			var errorBody = await response.Content.ReadAsStringAsync();
			throw new Exception($"Device authorization failed: {response.StatusCode} {errorBody}");
		}
		var result = await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();
		Assert.NotNull(result);
		Assert.NotNull(result.DeviceCode);
		Assert.NotEmpty(result.DeviceCode);
		Assert.NotNull(result.UserCode);
		Assert.NotEmpty(result.UserCode);
		Assert.True(result.ExpiresIn > 0);
		Assert.True(result.Interval >= 0);
	}

	[Fact]
	public async Task DeviceToken_BeforeVerification_ReturnsAuthorizationPending()
	{
		// Arrange
		await CreateDeviceCodeClientAsync();

		// Get device code
		var deviceResponse = await _client.PostAsync("/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["client_id"] = "device-client",
			["scope"] = "openid profile"
		}));
		var deviceResult = await deviceResponse.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();

		// Act - poll for token before user verification
		var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
			["device_code"] = deviceResult!.DeviceCode!,
			["client_id"] = "device-client"
		}));

		// Assert - should return 400 with authorization_pending
		Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
		var errorBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("authorization_pending", errorBody.GetProperty("error").GetString());
	}

	[Fact]
	public async Task DeviceCodeFlow_FullRoundtrip()
	{
		// Arrange
		await CreateDeviceCodeClientAsync();

		// Step 1: Device requests authorization
		var deviceResponse = await _client.PostAsync("/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["client_id"] = "device-client",
			["scope"] = "openid profile"
		}));
		Assert.Equal(HttpStatusCode.OK, deviceResponse.StatusCode);
		var deviceResult = await deviceResponse.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();
		Assert.NotNull(deviceResult?.UserCode);

		// Step 2: User logs in and approves the device code
		await _factory.CreateTestUserAsync("deviceuser", "Test123!@#");
		await _client.LoginAsync("deviceuser", "Test123!@#", _factory.JsonOptions);

		// Step 3: User verifies the device code
		var verifyResponse = await _client.PostAsync("/connect/verify",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["user_code"] = deviceResult.UserCode!
			}));

		// Verification should succeed (200 or redirect)
		Assert.True(verifyResponse.StatusCode == HttpStatusCode.OK ||
		            verifyResponse.StatusCode == HttpStatusCode.Redirect,
			$"Verification returned {verifyResponse.StatusCode}");

		// Step 4: Device polls for token (should now succeed)
		using var pollingClient = _factory.CreateClientWithCookies();
		var tokenResponse = await pollingClient.PostAsync("/connect/token",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
				["device_code"] = deviceResult.DeviceCode!,
				["client_id"] = "device-client"
			}));

		Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
		var tokens = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
		Assert.NotNull(tokens?.AccessToken);
		Assert.NotEmpty(tokens.AccessToken);
	}
}
