using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.Auth)]
public class LockoutTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public LockoutTests(SharedPostgresFixture fixture)
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

    private async Task LoginAsAdminAsync()
    {
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
    }

    #region Admin Unlock Tests

    [Fact]
    public async Task UnlockUser_WithLockedOutUser_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await CreateLockedOutUserAsync();
        var shortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.PostAsync($"/system/api/admin/users/{shortGuid}/unlock", null);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify user is no longer locked out
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var updatedUser = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(updatedUser);
        Assert.False(await userManager.IsLockedOutAsync(updatedUser));
    }

    [Fact]
    public async Task UnlockUser_WithNonLockedOutUser_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("notlocked");
        var shortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.PostAsync($"/system/api/admin/users/{shortGuid}/unlock", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnlockUser_WithNonExistentUser_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.PostAsync($"/system/api/admin/users/{nonExistentId}/unlock", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnlockUser_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        var user = await CreateLockedOutUserAsync();
        var shortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.PostAsync($"/system/api/admin/users/{shortGuid}/unlock", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnlockUser_WithNonAdminUser_ReturnsForbidden()
    {
        // Arrange
        var regularUser = await _factory.CreateTestUserAsync("regularuser", "Test123!@#", isAdmin: false);
        await _client.LoginAsync("regularuser", "Test123!@#", _factory.JsonOptions);

        var lockedUser = await CreateLockedOutUserAsync("lockeduser");
        var shortGuid = new ShortGuid(lockedUser.Id);

        // Act
        var response = await _client.PostAsync($"/system/api/admin/users/{shortGuid}/unlock", null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Lockout Functionality Tests

    [Fact]
    public async Task Login_WithMultipleFailedAttempts_LocksOutUser()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("lockouttest", password);

        // Act - Make 5 failed login attempts (default max failed access attempts)
        for (int i = 0; i < 5; i++)
        {
            await _client.LoginAsync(user.UserName!, "WrongPassword123!", _factory.JsonOptions);
        }

        // Try to login with correct password
        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.True(result.IsLockedOut);
    }

    [Fact]
    public async Task UnlockUser_AllowsLoginAfterUnlock()
    {
        // Arrange
        await LoginAsAdminAsync();
        var password = "Test123!@#";
        var user = await CreateLockedOutUserAsync("unlocklogin", password);
        var shortGuid = new ShortGuid(user.Id);

        // Act - Unlock the user
        var unlockResponse = await _client.PostAsync($"/system/api/admin/users/{shortGuid}/unlock", null);
        Assert.Equal(HttpStatusCode.NoContent, unlockResponse.StatusCode);

        // Logout admin
        await _client.PostAsync("/system/api/auth/logout", null);

        // Try to login as the unlocked user
        var loginResponse = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var result = await loginResponse.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
    }

    #endregion

    #region Login Audit Trail Tests

    [Fact]
    public async Task Login_WithSuccess_RecordsLoginEvent()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("auditlogin", password);

        // Act
        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert - verify login succeeded
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.True(result?.Succeeded);

        // Verify event was recorded
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var events = await session.Events.FetchStreamAsync(user.Id);

        var loginEvent = events
            .Select(e => e.Data)
            .OfType<UserLoggedIn>()
            .FirstOrDefault();

        Assert.NotNull(loginEvent);
        Assert.Equal(user.Id, loginEvent.UserId);
    }

    [Fact]
    public async Task Login_WithFailure_RecordsLoginFailedEvent()
    {
        // Arrange
        var user = await _factory.CreateTestUserAsync("auditfail");

        // Act
        var response = await _client.LoginAsync(user.UserName!, "WrongPassword123!", _factory.JsonOptions);

        // Assert - verify login failed
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.False(result?.Succeeded);

        // Verify event was recorded
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var events = await session.Events.FetchStreamAsync(user.Id);

        var loginFailedEvent = events
            .Select(e => e.Data)
            .OfType<UserLoginFailed>()
            .FirstOrDefault();

        Assert.NotNull(loginFailedEvent);
        Assert.Equal(user.Id, loginFailedEvent.UserId);
        Assert.Equal(LoginFailureReason.InvalidPassword, loginFailedEvent.FailureReason);
    }

    [Fact]
    public async Task Login_WhenLockedOut_RecordsLockoutEvent()
    {
        // Arrange
        var user = await _factory.CreateTestUserAsync("lockoutevent");

        // Act - Make 5 failed login attempts to trigger lockout
        for (int i = 0; i < 5; i++)
        {
            await _client.LoginAsync(user.UserName!, "WrongPassword123!", _factory.JsonOptions);
        }

        // Try one more time to record the lockout event
        await _client.LoginAsync(user.UserName!, "WrongPassword123!", _factory.JsonOptions);

        // Verify events were recorded
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var events = await session.Events.FetchStreamAsync(user.Id);

        // Should have lockout events
        var lockoutEvents = events
            .Select(e => e.Data)
            .OfType<UserLockedOut>()
            .ToList();

        Assert.NotEmpty(lockoutEvents);
        Assert.Contains(lockoutEvents, e => e.Reason == LockoutReason.TooManyFailedAttempts);
    }

    [Fact]
    public async Task Login_WithInactiveUser_RecordsLoginFailedEvent()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("inactiveaudit", password);

        // Make user inactive
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            user.SetIsActive(false);
            await userManager.UpdateAsync(user);
        }

        // Act
        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.False(result?.Succeeded);
        Assert.True(result?.IsNotAllowed);

        // Verify event was recorded
        using var verifyScope = _factory.Services.CreateScope();
        var session = verifyScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var events = await session.Events.FetchStreamAsync(user.Id);

        var loginFailedEvent = events
            .Select(e => e.Data)
            .OfType<UserLoginFailed>()
            .FirstOrDefault();

        Assert.NotNull(loginFailedEvent);
        Assert.Equal(LoginFailureReason.AccountInactive, loginFailedEvent.FailureReason);
    }

    #endregion

    #region Helper Methods

    private async Task<ApplicationUser> CreateLockedOutUserAsync(string userName = "lockeduser", string password = "Test123!@#")
    {
        var user = await _factory.CreateTestUserAsync(userName, password);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Lock out the user
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(30));
        await userManager.SetLockoutEnabledAsync(user, true);

        return user;
    }

    #endregion
}
