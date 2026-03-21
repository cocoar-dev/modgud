using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class AdminUserLifecycleTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public AdminUserLifecycleTests(SharedPostgresFixture fixture)
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

    #region Soft Delete Tests

    [Fact]
    public async Task SoftDelete_WithAdminRole_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("softdeluser");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Test soft delete" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SoftDelete_WithNonAdmin_ReturnsForbidden()
    {
        // Arrange
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("regularuser", password, isAdmin: false);
        await _client.LoginAsync("regularuser", password, _factory.JsonOptions);

        var targetUser = await _factory.CreateTestUserAsync("softdeltarget");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Unauthorized attempt" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SoftDelete_NonExistentUser_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{nonExistentId}/soft-delete",
            new AdminSoftDeleteDto { Reason = "No such user" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Restore Tests

    [Fact]
    public async Task Restore_SoftDeletedUser_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("restoreuser");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Soft delete first
        await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Will restore" },
            _factory.JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Restoring user" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Restore_NonDeletedUser_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("notdeleteduser");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act - try to restore a user that is not soft-deleted
        var response = await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Not deleted" },
            _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Permanent Erase Tests

    [Fact]
    public async Task PermanentErase_WithReason_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("eraseuser", email: "erase@test.com");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        request.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "GDPR request" }, options: _factory.JsonOptions);
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PermanentErase_AlreadyMasked_ReturnsConflict()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("erasedoubleuser", email: "erasedouble@test.com");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Erase once
        var firstRequest = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        firstRequest.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "First GDPR erase" }, options: _factory.JsonOptions);
        var firstResponse = await _client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        // Act - try to erase again
        var secondRequest = new HttpRequestMessage(HttpMethod.Delete, $"/system/api/admin/users/{shortGuid}/permanent");
        secondRequest.Content = JsonContent.Create(new AdminPermanentEraseDto { Reason = "Second attempt" }, options: _factory.JsonOptions);
        var secondResponse = await _client.SendAsync(secondRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    #endregion

    #region Get User After Lifecycle Operations

    [Fact]
    public async Task GetUser_AfterSoftDelete_ShowsDeleted()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("showdeleteduser");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Soft delete
        await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Check status" },
            _factory.JsonOptions);

        // Act - check deletion status via the admin deletion-status endpoint
        var statusResponse = await _client.GetAsync($"/system/api/admin/users/{shortGuid}/deletion-status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.ReadFromJsonAsync<DeletionStatusDto>(_factory.JsonOptions);
        Assert.NotNull(status);
        Assert.True(status.IsDeleted);
    }

    [Fact]
    public async Task GetUser_AfterRestore_ShowsActive()
    {
        // Arrange
        await LoginAsAdminAsync();
        var targetUser = await _factory.CreateTestUserAsync("restoredactiveuser");
        var shortGuid = new ShortGuid(targetUser.Id);

        // Soft delete then restore
        await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/soft-delete",
            new AdminSoftDeleteDto { Reason = "Will restore" },
            _factory.JsonOptions);

        await _client.PostAsJsonAsync(
            $"/system/api/admin/users/{shortGuid}/restore",
            new AdminRestoreDto { Reason = "Restoring" },
            _factory.JsonOptions);

        // Act - check deletion status
        var statusResponse = await _client.GetAsync($"/system/api/admin/users/{shortGuid}/deletion-status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.ReadFromJsonAsync<DeletionStatusDto>(_factory.JsonOptions);
        Assert.NotNull(status);
        Assert.False(status.IsDeleted);

        // Verify the deletion-status confirms restored state
        Assert.False(status.IsDataMasked);
    }

    #endregion
}
