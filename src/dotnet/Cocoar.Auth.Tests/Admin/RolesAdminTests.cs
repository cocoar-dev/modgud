using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.Admin)]
public class RolesAdminTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public RolesAdminTests(SharedPostgresFixture fixture)
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

    [Fact]
    public async Task GetRoles_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/roles");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_WithNonAdminUser_ReturnsForbidden()
    {
        // Arrange
        var user = await _factory.CreateTestUserAsync(isAdmin: false);
        await _client.LoginAsync(user.UserName, "Test123!@#", _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/api/admin/roles");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_AsAdmin_ReturnsRoleList()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestRoleAsync("Role1");
        await _factory.CreateTestRoleAsync("Role2");

        // Act
        var response = await _client.GetAsync("/api/admin/roles");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 3); // Admin role + 2 test roles
    }

    [Fact]
    public async Task GetRole_WithValidId_ReturnsRole()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("TargetRole", "A test role");
        var shortGuid = new ShortGuid(role.Id);

        // Act
        var response = await _client.GetAsync($"/api/admin/roles/{shortGuid}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("TargetRole", result.Name);
        Assert.Equal("A test role", result.Description);
    }

    [Fact]
    public async Task GetRole_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.GetAsync($"/api/admin/roles/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateRole_WithValidData_ReturnsCreatedRole()
    {
        // Arrange
        await LoginAsAdminAsync();
        var createDto = new CreateRoleDto
        {
            Name = "NewRole",
            Description = "A new role"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/roles", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("NewRole", result.Name);
        Assert.Equal("A new role", result.Description);
    }

    [Fact]
    public async Task CreateRole_WithDuplicateName_ReturnsConflict()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestRoleAsync("ExistingRole");

        var createDto = new CreateRoleDto
        {
            Name = "ExistingRole"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/roles", createDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRole_WithValidData_ReturnsUpdatedRole()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("UpdateMe", "Old description");
        var shortGuid = new ShortGuid(role.Id);

        var updateDto = new { Name = "UpdatedRole", Description = "New description" };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/admin/roles/{shortGuid}", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("UpdatedRole", result.Name);
        Assert.Equal("New description", result.Description);
    }

    [Fact]
    public async Task UpdateRole_WithPartialData_OnlyUpdatesProvidedFields()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("PartialUpdate", "Original description");
        var shortGuid = new ShortGuid(role.Id);

        // Only update description
        var updateDto = new { Description = "Updated description only" };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/admin/roles/{shortGuid}", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("PartialUpdate", result.Name); // Should remain unchanged
        Assert.Equal("Updated description only", result.Description);
    }

    [Fact]
    public async Task UpdateRole_WithDuplicateName_ReturnsConflict()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestRoleAsync("ExistingName");
        var role = await _factory.CreateTestRoleAsync("OriginalName");
        var shortGuid = new ShortGuid(role.Id);

        var updateDto = new { Name = "ExistingName" };

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/admin/roles/{shortGuid}", updateDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRole_WithValidIdAndNoUsers_ReturnsNoContent()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("DeleteMe");
        var shortGuid = new ShortGuid(role.Id);

        // Act
        var response = await _client.DeleteAsync($"/api/admin/roles/{shortGuid}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify role is deleted
        var getResponse = await _client.GetAsync($"/api/admin/roles/{shortGuid}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteRole_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();
        var nonExistentId = new ShortGuid(Guid.NewGuid());

        // Act
        var response = await _client.DeleteAsync($"/api/admin/roles/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRole_WithAssignedUsers_ReturnsBadRequest()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("RoleWithUser");

        // Create a user with this role
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Cocoar.Auth.Domain.Entities.ApplicationUser>>();
        var user = new Cocoar.Auth.Domain.Entities.ApplicationUser("userwithRole", "user@test.com");
        user.AddRole(role.Id);
        await userManager.CreateAsync(user, "Password123!@#");

        var shortGuid = new ShortGuid(role.Id);

        // Act
        var response = await _client.DeleteAsync($"/api/admin/roles/{shortGuid}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
