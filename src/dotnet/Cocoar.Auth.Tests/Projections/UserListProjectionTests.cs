using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Projections;

/// <summary>
/// Tests for UserListProjection async projection.
/// Verifies that the denormalized user list stays consistent with user and role changes.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Projections")]
public class UserListProjectionTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public UserListProjectionTests(SharedPostgresFixture fixture)
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
    public async Task UserList_ContainsCreatedUser()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestUserAsync("listuser");

        // Act
        var response = await _client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Contains(result.Items, u => u.UserName == "listuser");
    }

    [Fact]
    public async Task UserList_ExcludesSoftDeletedUsers()
    {
        // Arrange
        await LoginAsAdminAsync();
        var user = await _factory.CreateTestUserAsync("todelete");
        var shortGuid = new ShortGuid(user.Id);

        // Soft-delete via GDPR endpoint
        await _client.PostAsync($"/api/admin/users/{shortGuid}/soft-delete", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // Act
        var response = await _client.GetAsync("/api/admin/users");

        // Assert
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, u => u.UserName == "todelete");
    }

    [Fact]
    public async Task UserList_ReflectsRoleAssignment()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("Editor");
        var user = await _factory.CreateTestUserAsync("editoruser");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "Editor");
        }

        // Act
        var response = await _client.GetAsync("/api/admin/users");
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);

        // Assert
        Assert.NotNull(result);
        var listUser = result.Items.FirstOrDefault(u => u.UserName == "editoruser");
        Assert.NotNull(listUser);
        Assert.Contains(listUser.Roles, r => r.Guid == role.Id);
    }

    [Fact]
    public async Task UserList_ReflectsRoleNameChange()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("OldName");
        var roleShortGuid = new ShortGuid(role.Id);
        var user = await _factory.CreateTestUserAsync("renametest");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "OldName");
        }

        // Rename the role
        var updateDto = new Application.DTOs.Roles.UpdateRoleDto
        {
            Name = new Optional<string>("NewName")
        };
        await _client.PatchAsJsonAsync($"/api/admin/roles/{roleShortGuid}", updateDto, _factory.JsonOptions);

        // Act — verify via repository that the UserListReadModel has the updated name
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUserListRepository>();
            var (users, _) = await repo.GetPagedAsync(1, 100);

            var listUser = users.FirstOrDefault(u => u.UserName == "renametest");
            Assert.NotNull(listUser);
            Assert.Contains(listUser.Roles, r => r.Name == "NewName");
            Assert.DoesNotContain(listUser.Roles, r => r.Name == "OldName");
        }
    }
}
