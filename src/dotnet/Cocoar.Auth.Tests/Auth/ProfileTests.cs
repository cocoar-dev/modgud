using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class ProfileTests : IAsyncLifetime
{
    private readonly CocoarAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProfileTests(SharedPostgresFixture fixture)
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

    [Fact]
    public async Task GetProfile_WhenAuthenticated_ReturnsProfile()
    {
        // Arrange
        await _factory.CreateTestUserAsync("profileuser", email: "profileuser@test.com");
        await _client.LoginAsync("profileuser", "Test123!@#", _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/profile");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(_factory.JsonOptions);
        Assert.NotNull(profile);
        Assert.Equal("profileuser", profile.UserName);
        Assert.Equal("profileuser@test.com", profile.Email);
        Assert.Equal("Test", profile.FirstName);
        Assert.Equal("User", profile.LastName);
    }

    [Fact]
    public async Task GetProfile_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/system/api/auth/profile");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_WhenAuthenticated_UpdatesAndReturnsProfile()
    {
        // Arrange
        await _factory.CreateTestUserAsync("updateprofile", email: "updateprofile@test.com");
        await _client.LoginAsync("updateprofile", "Test123!@#", _factory.JsonOptions);

        // Act
        var updateDto = new UpdateProfileDto
        {
            FirstName = "Updated",
            LastName = "Name",
            PhoneNumber = "+1234567890"
        };
        var response = await _client.PutAsJsonAsync("/system/api/auth/profile", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(_factory.JsonOptions);
        Assert.NotNull(profile);
        Assert.Equal("Updated", profile.FirstName);
        Assert.Equal("Name", profile.LastName);
        Assert.Equal("+1234567890", profile.PhoneNumber);
    }

    [Fact]
    public async Task UpdateProfile_PartialUpdate_OnlyUpdatesProvidedFields()
    {
        // Arrange
        await _factory.CreateTestUserAsync("partialupdate", email: "partialupdate@test.com");
        await _client.LoginAsync("partialupdate", "Test123!@#", _factory.JsonOptions);

        // Act - Only update first name
        var updateDto = new UpdateProfileDto
        {
            FirstName = "OnlyFirst"
        };
        var response = await _client.PutAsJsonAsync("/system/api/auth/profile", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(_factory.JsonOptions);
        Assert.NotNull(profile);
        Assert.Equal("OnlyFirst", profile.FirstName);
        Assert.Equal("User", profile.LastName); // Should remain unchanged
    }

    [Fact]
    public async Task UpdateProfile_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var updateDto = new UpdateProfileDto { FirstName = "Test" };
        var response = await _client.PutAsJsonAsync("/system/api/auth/profile", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_Succeeds()
    {
        // Arrange
        await _factory.CreateTestUserAsync("changepassword", email: "changepassword@test.com");
        await _client.LoginAsync("changepassword", "Test123!@#", _factory.JsonOptions);

        // Act
        var changeDto = new ChangePasswordDto
        {
            CurrentPassword = "Test123!@#",
            NewPassword = "NewPass123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/change-password", changeDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify new password works
        await _client.PostAsync("/system/api/auth/logout", null);
        var loginResponse = await _client.LoginAsync("changepassword", "NewPass123!@#", _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithInvalidCurrentPassword_ReturnsBadRequest()
    {
        // Arrange
        await _factory.CreateTestUserAsync("wrongpassword", email: "wrongpassword@test.com");
        await _client.LoginAsync("wrongpassword", "Test123!@#", _factory.JsonOptions);

        // Act
        var changeDto = new ChangePasswordDto
        {
            CurrentPassword = "WrongPassword!@#",
            NewPassword = "NewPass123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/change-password", changeDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var changeDto = new ChangePasswordDto
        {
            CurrentPassword = "Test123!@#",
            NewPassword = "NewPass123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/change-password", changeDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMe_WhenAuthenticated_ReturnsCurrentUser()
    {
        // Arrange
        await _factory.CreateTestUserAsync("meuser", email: "meuser@test.com", isAdmin: true);
        await _client.LoginAsync("meuser", "Test123!@#", _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var currentUser = await response.Content.ReadFromJsonAsync<CurrentUserDto>(_factory.JsonOptions);
        Assert.NotNull(currentUser);
        Assert.Equal("meuser", currentUser.UserName);
        Assert.Contains("Admin", currentUser.Roles);
    }
}
