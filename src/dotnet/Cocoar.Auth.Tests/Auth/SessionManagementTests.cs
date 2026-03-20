using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class SessionManagementTests : IAsyncLifetime
{
    private readonly CocoarAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionManagementTests(SharedPostgresFixture fixture)
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

    #region Get Sessions Tests

    [Fact]
    public async Task GetSessions_WhenAuthenticated_ReturnsSessionList()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("sessionuser", password);

        // Create a session for the user
        await CreateSessionForUserAsync(user.Id);

        // Login to authenticate
        await _client.LoginAsync("sessionuser", password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SessionListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.NotEmpty(result.Sessions);
    }

    [Fact]
    public async Task GetSessions_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/system/api/auth/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_ReturnsCorrectSessionDetails()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("detailsuser", password);

        // Create a session with specific details
        var session = await CreateSessionForUserAsync(user.Id, "192.168.1.100", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

        await _client.LoginAsync("detailsuser", password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SessionListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        // Login now creates a session automatically, so we expect 2 sessions:
        // 1 manually created + 1 from login
        Assert.Equal(2, result.Sessions.Count);

        var manualSession = result.Sessions.First(s => s.Id == session.Id.ToString());
        Assert.Equal("192.168.1.100", manualSession.IpAddress);
    }

    #endregion

    #region Revoke Session Tests

    [Fact]
    public async Task RevokeSession_WithOwnSession_ReturnsNoContent()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("revokeuser", password);
        var session = await CreateSessionForUserAsync(user.Id);

        await _client.LoginAsync("revokeuser", password, _factory.JsonOptions);

        // Act
        var response = await _client.DeleteAsync($"/system/api/auth/sessions/{session.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify session is deleted
        using var scope = _factory.Services.CreateScope();
        var documentSession = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var deletedSession = await documentSession.LoadAsync<UserSession>(session.Id);
        Assert.Null(deletedSession);
    }

    [Fact]
    public async Task RevokeSession_WithNonExistentSession_ReturnsNotFound()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("revokenonexistent", password);
        await _client.LoginAsync("revokenonexistent", password, _factory.JsonOptions);

        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/system/api/auth/sessions/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RevokeSession_WithOtherUserSession_ReturnsForbidden()
    {
        // Arrange - Create two users
        var password = "Test123!@#";
        var user1 = await _factory.CreateTestUserAsync("user1", password);
        var user2 = await _factory.CreateTestUserAsync("user2", password);

        // Create session for user2
        var user2Session = await CreateSessionForUserAsync(user2.Id);

        // Login as user1
        await _client.LoginAsync("user1", password, _factory.JsonOptions);

        // Act - Try to revoke user2's session
        var response = await _client.DeleteAsync($"/system/api/auth/sessions/{user2Session.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokeSession_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.DeleteAsync($"/system/api/auth/sessions/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Revoke All Sessions Tests

    [Fact]
    public async Task RevokeAllSessions_RemovesAllExceptCurrentSession()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("revokealluser", password);

        // Create multiple sessions
        await CreateSessionForUserAsync(user.Id);
        await CreateSessionForUserAsync(user.Id);
        await CreateSessionForUserAsync(user.Id);

        await _client.LoginAsync("revokealluser", password, _factory.JsonOptions);

        // Act
        var response = await _client.DeleteAsync("/system/api/auth/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify only the current session (created during login) remains
        using var scope = _factory.Services.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var remainingSessions = await sessionRepository.GetByUserIdAsync(user.Id);
        Assert.Single(remainingSessions);
    }

    [Fact]
    public async Task RevokeAllSessions_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.DeleteAsync("/system/api/auth/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Admin Session Tests

    [Fact]
    public async Task AdminGetUserSessions_ReturnsUserSessions()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("targetuser");
        await CreateSessionForUserAsync(targetUser.Id);
        await CreateSessionForUserAsync(targetUser.Id);

        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.GetAsync($"/system/api/admin/users/{shortGuid}/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SessionListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public async Task AdminGetUserSessions_WithoutAdminRole_ReturnsForbidden()
    {
        // Arrange
        var password = "Test123!@#";
        var regularUser = await _factory.CreateTestUserAsync("regularadmin", password, isAdmin: false);
        await _client.LoginAsync("regularadmin", password, _factory.JsonOptions);

        var targetUser = await _factory.CreateTestUserAsync("admintarget");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.GetAsync($"/system/api/admin/users/{shortGuid}/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminRevokeAllUserSessions_RemovesAllSessions()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("admintargetrevoke");
        await CreateSessionForUserAsync(targetUser.Id);
        await CreateSessionForUserAsync(targetUser.Id);

        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.DeleteAsync($"/system/api/admin/users/{shortGuid}/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify all sessions are deleted
        using var scope = _factory.Services.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var remainingSessions = await sessionRepository.GetByUserIdAsync(targetUser.Id);
        Assert.Empty(remainingSessions);
    }

    [Fact]
    public async Task AdminRevokeAllUserSessions_WithoutAdminRole_ReturnsForbidden()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("nonadminrevoke", password, isAdmin: false);
        await _client.LoginAsync("nonadminrevoke", password, _factory.JsonOptions);

        var targetUser = await _factory.CreateTestUserAsync("targetrevoke");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.DeleteAsync($"/system/api/admin/users/{shortGuid}/sessions");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Helper Methods

    private async Task LoginAsAdminAsync()
    {
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
    }

    private async Task<UserSession> CreateSessionForUserAsync(
        Guid userId,
        string? ipAddress = "127.0.0.1",
        string? userAgent = "TestClient/1.0")
    {
        using var scope = _factory.Services.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();

        var result = await sessionService.CreateSessionAsync(userId, ipAddress, userAgent);
        Assert.False(result.IsError, $"Failed to create session: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        return result.Value;
    }

    #endregion
}
