using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Admin;

public class UsersAdminTests : IClassFixture<CocoarAuthWebApplicationFactory>, IAsyncLifetime
{
    private readonly CocoarAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UsersAdminTests(CocoarAuthWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClientWithCookies();
    }

    public async Task InitializeAsync()
    {
        await _factory.CleanDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task LoginAsAdminAsync()
    {
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
    }

    [Fact]
    public async Task GetUsers_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_WithNonAdminUser_ReturnsForbidden()
    {
        // Arrange
        var user = await _factory.CreateTestUserAsync(isAdmin: false);
        await _client.LoginAsync(user.UserName, "Test123!@#", _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_AsAdmin_ReturnsUserList()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestUserAsync("user1");
        await _factory.CreateTestUserAsync("user2");

        // Act
        var response = await _client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 3); // admin + 2 test users
    }

    [Fact]
    public async Task GetUsers_WithSearch_ReturnsFilteredList()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestUserAsync("searchable");
        await _factory.CreateTestUserAsync("other");

        // Act
        var response = await _client.GetAsync("/api/admin/users?search=searchable");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.Equal("searchable", result.Items[0].UserName);
    }

    [Fact]
    public async Task GetUser_WithValidId_ReturnsUser()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("targetuser");
        var shortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.GetAsync($"/api/admin/users/{shortGuid}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("targetuser", result.UserName);
    }

    [Fact]
    public async Task GetUser_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.GetAsync($"/api/admin/users/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithValidData_ReturnsCreatedUser()
    {
        // Arrange
        await LoginAsAdminAsync();
        var createDto = new CreateUserDto
        {
            UserName = "newuser",
            Password = "NewUser123!@#",
            Email = "newuser@test.com",
            FirstName = "New",
            LastName = "User",
            IsActive = true,
            LockoutEnabled = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/users", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("newuser", result.UserName);
        Assert.Equal("newuser@test.com", result.Email);
        Assert.Equal("New", result.FirstName);
        Assert.Equal("User", result.LastName);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task CreateUser_WithDuplicateUserName_ReturnsConflict()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestUserAsync("existinguser");

        var createDto = new CreateUserDto
        {
            UserName = "existinguser",
            Password = "NewUser123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/users", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithWeakPassword_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();
        var createDto = new CreateUserDto
        {
            UserName = "weakpassuser",
            Password = "weak" // Too weak
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/users", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_WithValidData_ReturnsUpdatedUser()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("updateme");
        var shortGuid = new ShortGuid(user.Id);

        var updateDto = new { FirstName = "Updated", LastName = "Name" };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/admin/users/{shortGuid}", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("Updated", result.FirstName);
        Assert.Equal("Name", result.LastName);
    }

    [Fact]
    public async Task UpdateUser_WithPartialData_OnlyUpdatesProvidedFields()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("partialupdate");
        var shortGuid = new ShortGuid(user.Id);

        // Only update FirstName
        var updateDto = new { FirstName = "OnlyFirst" };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/admin/users/{shortGuid}", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("OnlyFirst", result.FirstName);
        Assert.Equal("User", result.LastName); // Should remain unchanged
    }

    [Fact]
    public async Task DeleteUser_WithValidId_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("deleteme");
        var shortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.DeleteAsync($"/api/admin/users/{shortGuid}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify user is deleted
        var getResponse = await _client.GetAsync($"/api/admin/users/{shortGuid}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.DeleteAsync($"/api/admin/users/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithValidId_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("resetpwduser", "OldPassword123!@#");
        var shortGuid = new ShortGuid(user.Id);

        var resetDto = new { NewPassword = "NewPassword456!@#" };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/admin/users/{shortGuid}/reset-password", resetDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify new password works
        await _client.PostAsync("/api/auth/logout", null);
        var loginResponse = await _client.LoginAsync("resetpwduser", "NewPassword456!@#", _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithRoles_AssignsRoles()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("TestRole");
        var roleShortGuid = new ShortGuid(role.Id);

        var createDto = new CreateUserDto
        {
            UserName = "userwithRole",
            Password = "Password123!@#",
            Roles = [roleShortGuid]
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/users", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Single(result.Roles);
        Assert.Equal(roleShortGuid.Value, result.Roles[0].Value);
    }
}
