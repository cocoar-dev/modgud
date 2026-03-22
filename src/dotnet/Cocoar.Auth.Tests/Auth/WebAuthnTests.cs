using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.TwoFactor)]
public class WebAuthnTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public WebAuthnTests(SharedPostgresFixture fixture)
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

    #region Registration Options Tests

    [Fact]
    public async Task GetRegistrationOptions_Unauthenticated_Returns401()
    {
        // Act
        var response = await _client.PostAsync("/system/api/auth/webauthn/register/options", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRegistrationOptions_Authenticated_ReturnsOptions()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("webauthnreguser", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/system/api/auth/webauthn/register/options", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<WebAuthnRegistrationOptionsDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.NotEqual(default, result.Options);
    }

    #endregion

    #region Complete Registration Tests

    [Fact]
    public async Task CompleteRegistration_Unauthenticated_Returns401()
    {
        // Arrange
        var dto = new CompleteWebAuthnRegistrationDto
        {
            AttestationResponse = JsonSerializer.SerializeToElement(new { id = "fake", rawId = "fake", type = "public-key" }),
            DeviceName = "Test Device"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/auth/webauthn/register/complete", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CompleteRegistration_WithInvalidAttestationResponse_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("webauthnbadattest", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // First get registration options to create a challenge
        await _client.PostAsync("/system/api/auth/webauthn/register/options", null);

        var dto = new CompleteWebAuthnRegistrationDto
        {
            AttestationResponse = JsonSerializer.SerializeToElement(new { invalid = "garbage-data", foo = "bar" }),
            DeviceName = "Bad Device"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/auth/webauthn/register/complete", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Credentials Tests

    [Fact]
    public async Task GetCredentials_Unauthenticated_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/system/api/auth/webauthn/credentials");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCredentials_Authenticated_ReturnsEmptyList()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("webauthnnocreds", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/webauthn/credentials");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<WebAuthnCredentialListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Empty(result.Credentials);
    }

    #endregion

    #region Delete Credential Tests

    [Fact]
    public async Task DeleteCredential_Unauthenticated_Returns401()
    {
        // Act
        var response = await _client.DeleteAsync("/system/api/auth/webauthn/credentials/nonexistent-id");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCredential_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("webauthndelcred", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.DeleteAsync("/system/api/auth/webauthn/credentials/nonexistent-credential-id");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Rename Credential Tests

    [Fact]
    public async Task RenameCredential_Unauthenticated_Returns401()
    {
        // Arrange
        var dto = new RenameWebAuthnCredentialDto { Name = "New Name" };

        // Act
        var response = await _client.PatchAsJsonAsync("/system/api/auth/webauthn/credentials/some-id", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RenameCredential_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("webauthrenname", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        var dto = new RenameWebAuthnCredentialDto { Name = "New Name" };

        // Act
        var response = await _client.PatchAsJsonAsync("/system/api/auth/webauthn/credentials/nonexistent-id", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Login Options Tests

    [Fact]
    public async Task GetLoginOptions_ReturnsOptions()
    {
        // Act - should work without authentication (passwordless flow)
        var dto = new WebAuthnLoginOptionsRequestDto();
        var response = await _client.PostAsJsonAsync("/system/api/auth/webauthn/login/options", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<WebAuthnAuthenticationOptionsDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.NotEqual(default, result.Options);
    }

    #endregion

    #region Complete Login Tests

    [Fact]
    public async Task CompleteLogin_WithInvalidAssertion_DoesNotSucceed()
    {
        // Act - send invalid assertion without prior login options
        var response = await _client.PostAsync("/system/api/auth/webauthn/login/complete",
            JsonContent.Create(new
            {
                assertionResponse = new { invalid = "garbage", id = "fake", rawId = "fake" },
                rememberMachine = false
            }));

        // Assert - should not succeed (400, 500, or OK with error)
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("false", body); // Succeeded = false
        }
        // Any non-200 is also acceptable (400, 500)
    }

    #endregion
}
