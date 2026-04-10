using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.Auth)]
public class AuthenticationTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public AuthenticationTests(SharedPostgresFixture fixture)
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

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: password);

        // Act
        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded, $"Login failed. Response: {content}");
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ReturnsFailed()
    {
        // Arrange
        var user = await _factory.CreateTestUserAsync();

        // Act
        var response = await _client.LoginAsync(user.UserName, "WrongPassword123!", _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Login_WithNonExistentUser_ReturnsFailed()
    {
        // Act
        var response = await _client.LoginAsync("nonexistent", "Password123!", _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Login_WithInactiveUser_ReturnsNotAllowed()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: password, isActive: false);

        // Act
        var response = await _client.LoginAsync(user.UserName, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.True(result.IsNotAllowed);
    }

    [Fact]
    public async Task GetCurrentUser_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentUser_AfterLogin_ReturnsUserInfo()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: password);

        var loginResponse = await _client.LoginAsync(user.UserName, password, _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(user.UserName, result.UserName);
        Assert.Equal(user.Email, result.Email);
    }

    [Fact]
    public async Task Logout_AfterLogin_ClearsSession()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: password);

        await _client.LoginAsync(user.UserName, password, _factory.JsonOptions);

        // Act
        var logoutResponse = await _client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        // Assert - should be unauthorized after logout
        var meResponse = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_Succeeds()
    {
        // Arrange
        var currentPassword = "Test123!@#";
        var newPassword = "NewTest456!@#";
        var user = await _factory.CreateTestUserAsync(password: currentPassword);

        await _client.LoginAsync(user.UserName, currentPassword, _factory.JsonOptions);

        var changePasswordDto = new { CurrentPassword = currentPassword, NewPassword = newPassword };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", changePasswordDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify new password works
        await _client.PostAsync("/api/auth/logout", null);
        var loginResponse = await _client.LoginAsync(user.UserName, newPassword, _factory.JsonOptions);
        var loginResult = await loginResponse.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.True(loginResult?.Succeeded);
    }

    [Fact]
    public async Task ChangePassword_WithInvalidCurrentPassword_Fails()
    {
        // Arrange
        var currentPassword = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: currentPassword);

        await _client.LoginAsync(user.UserName, currentPassword, _factory.JsonOptions);

        var changePasswordDto = new { CurrentPassword = "WrongPassword!", NewPassword = "NewTest456!@#" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", changePasswordDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
