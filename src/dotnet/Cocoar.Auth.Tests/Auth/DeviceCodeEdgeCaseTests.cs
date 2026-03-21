using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class DeviceCodeEdgeCaseTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public DeviceCodeEdgeCaseTests(SharedPostgresFixture fixture)
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

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; }
    }

    #region Device Authorization Edge Cases

    [Fact]
    public async Task DeviceAuthorization_WithInvalidClient_ReturnsBadRequest()
    {
        // Act - request device code with a client ID that doesn't exist
        var response = await _client.PostAsync("/system/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "nonexistent-client",
            ["scope"] = "openid profile"
        }));

        // Assert
        // OpenIddict returns 401 for unknown clients
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected BadRequest or Unauthorized, got {response.StatusCode}");
    }

    [Fact]
    public async Task DeviceAuthorization_WithClientWithoutDeviceGrant_ReturnsBadRequest()
    {
        // Arrange - create a client that does NOT have device_code grant type
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
        await _factory.SeedOpenIddictScopesAsync();

        var createResponse = await _client.PostAsJsonAsync("/system/api/admin/oauth/clients", new CreateOAuthClientDto
        {
            ClientId = "no-device-client",
            DisplayName = "No Device Grant Client",
            ClientType = "public",
            ConsentType = "implicit",
            AllowedGrantTypes = ["authorization_code"], // NOT device_code
            Scopes = ["openid", "profile"],
            RedirectUris = ["http://localhost/callback"]
        }, _factory.JsonOptions);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        await _client.PostAsync("/system/api/auth/logout", null);

        // Act - try to use device code flow with this client
        var response = await _client.PostAsync("/system/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "no-device-client",
            ["scope"] = "openid profile"
        }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Device Token Edge Cases

    [Fact]
    public async Task DeviceToken_WithInvalidDeviceCode_ReturnsBadRequest()
    {
        // Arrange - create a valid device code client first
        await CreateDeviceCodeClientAsync();

        // Act - try to exchange a bogus device code
        var tokenResponse = await _client.PostAsync("/system/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = "totally-invalid-device-code",
            ["client_id"] = "edge-device-client"
        }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
    }

    #endregion

    #region Device Verification Edge Cases

    [Fact]
    public async Task DeviceVerification_GET_RedirectsToFrontend()
    {
        // Arrange - create client and get a device code
        await CreateDeviceCodeClientAsync();

        var deviceResponse = await _client.PostAsync("/system/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "edge-device-client",
            ["scope"] = "openid profile"
        }));
        Assert.Equal(HttpStatusCode.OK, deviceResponse.StatusCode);
        var deviceResult = await deviceResponse.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();
        Assert.NotNull(deviceResult?.UserCode);

        // Login as a user
        await _factory.CreateTestUserAsync("deviceverifyuser", "Test123!@#");
        await _client.LoginAsync("deviceverifyuser", "Test123!@#", _factory.JsonOptions);

        // Act - GET the verification endpoint (should redirect to frontend /device page)
        var verifyResponse = await _client.GetAsync(
            $"/system/connect/verify?user_code={Uri.EscapeDataString(deviceResult.UserCode!)}");

        // Assert - should redirect to frontend device verification page
        Assert.Equal(HttpStatusCode.Redirect, verifyResponse.StatusCode);
        var location = verifyResponse.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("/device", location);
    }

    #endregion

    #region Helper Methods

    private async Task CreateDeviceCodeClientAsync()
    {
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
        await _factory.SeedOpenIddictScopesAsync();

        var response = await _client.PostAsJsonAsync("/system/api/admin/oauth/clients", new CreateOAuthClientDto
        {
            ClientId = "edge-device-client",
            DisplayName = "Edge Case Device Client",
            ClientType = "public",
            ConsentType = "implicit",
            AllowedGrantTypes = ["urn:ietf:params:oauth:grant-type:device_code"],
            Scopes = ["openid", "profile"]
        }, _factory.JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _client.PostAsync("/system/api/auth/logout", null);
    }

    #endregion
}
