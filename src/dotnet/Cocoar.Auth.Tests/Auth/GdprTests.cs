using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.GDPR)]
public class GdprTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public GdprTests(SharedPostgresFixture fixture)
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

    #region Data Export Tests

    [Fact]
    public async Task ExportData_WhenAuthenticated_ReturnsUserData()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("exportuser", password, email: "export@test.com");
        await _client.LoginAsync("exportuser", password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/export-data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UserDataExportDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Metadata.UserId);
        Assert.Equal("exportuser", result.Profile.UserName);
        Assert.Equal("export@test.com", result.Profile.Email);
    }

    [Fact]
    public async Task ExportData_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/system/api/auth/export-data");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Deletion Request Tests

    [Fact]
    public async Task RequestDeletion_WithValidPassword_ReturnsDeletionRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("deleteuser", password, email: "delete@test.com");
        await _client.LoginAsync("deleteuser", password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = password, Reason = "Test deletion" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DeletionRequestDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.ConfirmationDeadline > DateTimeOffset.UtcNow);

        // Verify state
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userState = await session.LoadAsync<UserState>(user.Id);
        Assert.NotNull(userState);
        Assert.True(userState.IsDeletionPending);
    }

    [Fact]
    public async Task RequestDeletion_WithInvalidPassword_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("deletewrong", password);
        await _client.LoginAsync("deletewrong", password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = "WrongPassword123!" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequestDeletion_WhenAlreadyPending_ReturnsConflict()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("deletepending", password, email: "pending@test.com");
        await _client.LoginAsync("deletepending", password, _factory.JsonOptions);

        // First request
        await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = password },
            _factory.JsonOptions);

        // Act - Second request
        var response = await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = password },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    #endregion

    #region Cancel Deletion Tests

    [Fact]
    public async Task CancelDeletion_WhenPending_ReturnsNoContent()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("canceluser", password, email: "cancel@test.com");
        await _client.LoginAsync("canceluser", password, _factory.JsonOptions);

        // Request deletion first
        await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = password },
            _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/system/api/auth/cancel-deletion", null);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify state
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userState = await session.LoadAsync<UserState>(user.Id);
        Assert.NotNull(userState);
        Assert.False(userState.IsDeletionPending);
    }

    [Fact]
    public async Task CancelDeletion_WhenNotPending_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("cancelnotpending", password);
        await _client.LoginAsync("cancelnotpending", password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/system/api/auth/cancel-deletion", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Deletion Status Tests

    [Fact]
    public async Task GetDeletionStatus_ReturnsCorrectStatus()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("statususer", password, email: "status@test.com");
        await _client.LoginAsync("statususer", password, _factory.JsonOptions);

        // Act - Before request
        var response1 = await _client.GetAsync("/system/api/auth/deletion-status");
        var status1 = await response1.Content.ReadFromJsonAsync<DeletionStatusDto>(_factory.JsonOptions);

        // Request deletion
        await _client.PostAsJsonAsync("/system/api/auth/delete-account",
            new RequestDeletionDto { Password = password },
            _factory.JsonOptions);

        // Act - After request
        var response2 = await _client.GetAsync("/system/api/auth/deletion-status");
        var status2 = await response2.Content.ReadFromJsonAsync<DeletionStatusDto>(_factory.JsonOptions);

        // Assert
        Assert.NotNull(status1);
        Assert.False(status1.IsPending);

        Assert.NotNull(status2);
        Assert.True(status2.IsPending);
        Assert.NotNull(status2.RequestedAt);
        Assert.NotNull(status2.ConfirmationDeadline);
    }

    #endregion

    #region Admin Soft Delete Tests

    [Fact]
    public async Task AdminSoftDelete_WithAdminRole_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("softdeletetarget");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Test soft delete" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify state
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userState = await session.LoadAsync<UserState>(targetUser.Id);
        Assert.NotNull(userState);
        Assert.True(userState.IsDeleted);
    }

    [Fact]
    public async Task AdminSoftDelete_WithNonAdminRole_ReturnsForbidden()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("nonadmindelete", password, isAdmin: false);
        await _client.LoginAsync("nonadmindelete", password, _factory.JsonOptions);

        var targetUser = await _factory.CreateTestUserAsync("deletetarget2");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Test" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Admin Restore Tests

    [Fact]
    public async Task AdminRestore_WithDeletedUser_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("restoretarget");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Soft delete first
        await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Test" },
            _factory.JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Test restore" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify state
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userState = await session.LoadAsync<UserState>(targetUser.Id);
        Assert.NotNull(userState);
        Assert.False(userState.IsDeleted);
    }

    [Fact]
    public async Task AdminRestore_WithNonDeletedUser_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("notdeleted");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Test" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Admin Permanent Erase Tests

    [Fact]
    public async Task AdminPermanentErase_ErasesUserData()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("erasetarget", email: "erase@test.com");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act - Send DELETE with body using SendAsync
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        request.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "GDPR request" }, options: _factory.JsonOptions);
        var eraseResponse = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, eraseResponse.StatusCode);

        // Verify ApplicationUser document is updated (this is done last in the deletion process)
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var querySession = store.QuerySession("system");

        var user = await querySession.LoadAsync<ApplicationUser>(targetUser.Id);
        Assert.NotNull(user);
        Assert.True(user.IsDeleted, "ApplicationUser should be marked as deleted");
        Assert.True(user.IsDataErased, "ApplicationUser data should be erased");
        Assert.Equal("[DELETED]", user.UserName);
    }

    [Fact]
    public async Task AdminPermanentErase_WhenAlreadyMasked_ReturnsConflict()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("erasedouble", email: "erasedouble@test.com");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Erase once
        var request1 = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        request1.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "First erase" }, options: _factory.JsonOptions);
        await _client.SendAsync(request1);

        // Act - Try to erase again
        var request2 = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        request2.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "Second erase" }, options: _factory.JsonOptions);
        var response = await _client.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AdminRestore_WhenDataMasked_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();

        var targetUser = await _factory.CreateTestUserAsync("norestoretarget", email: "norestore@test.com");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Permanently erase
        var eraseRequest = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        eraseRequest.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "GDPR request" }, options: _factory.JsonOptions);
        await _client.SendAsync(eraseRequest);

        // Act - Try to restore
        var response = await _client.PostAsJsonAsync($"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Restore attempt" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Helper Methods

    private async Task LoginAsAdminAsync()
    {
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
    }

    #endregion
}
