using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Projections;

/// <summary>
/// Tests for RoleListProjection async projection.
/// Verifies that the denormalized role list maintains correct user counts.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Projections")]
public class RoleListProjectionTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public RoleListProjectionTests(SharedPostgresFixture fixture)
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
    public async Task RoleList_ContainsCreatedRole()
    {
        // Arrange
        await LoginAsAdminAsync();
        await _factory.CreateTestRoleAsync("ListTestRole", "A test role");

        // Act
        var response = await _client.GetAsync("/api/admin/roles");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RoleListDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Contains(result.Items, r => r.Name == "ListTestRole");
    }

    [Fact]
    public async Task RoleList_ExcludesDeletedRoles()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("ToDeleteRole");
        var shortGuid = new ShortGuid(role.Id);

        // Delete the role
        await _client.DeleteAsync($"/api/admin/roles/{shortGuid}");

        // Act
        var response = await _client.GetAsync("/api/admin/roles");
        var result = await response.ReadFromJsonAsync<RoleListDto>(_factory.JsonOptions);

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, r => r.Name == "ToDeleteRole");
    }

    [Fact]
    public async Task RoleList_TracksUserCount()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("CountRole");

        var user1 = await _factory.CreateTestUserAsync("countuser1");
        var user2 = await _factory.CreateTestUserAsync("countuser2");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser1 = await userManager.FindByIdAsync(user1.Id.ToString());
            var appUser2 = await userManager.FindByIdAsync(user2.Id.ToString());
            await userManager.AddToRoleAsync(appUser1!, "CountRole");
            await userManager.AddToRoleAsync(appUser2!, "CountRole");
        }

        // Act — check user count via repository
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRoleListRepository>();
            var roles = await repo.GetAllAsync();

            var countRole = roles.FirstOrDefault(r => r.Name == "CountRole");
            Assert.NotNull(countRole);
            Assert.Equal(2, countRole.UserCount);
        }
    }

    [Fact]
    public async Task RoleList_DecreasesUserCountOnRoleRemoval()
    {
        // Arrange
        await LoginAsAdminAsync();
        var role = await _factory.CreateTestRoleAsync("RemoveCountRole");

        var user = await _factory.CreateTestUserAsync("removeuser");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "RemoveCountRole");
        }

        // Verify count is 1
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRoleListRepository>();
            var roles = await repo.GetAllAsync();
            Assert.Equal(1, roles.First(r => r.Name == "RemoveCountRole").UserCount);
        }

        // Remove role from user
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.RemoveFromRoleAsync(appUser!, "RemoveCountRole");
        }

        // Act
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRoleListRepository>();
            var roles = await repo.GetAllAsync();

            var countRole = roles.FirstOrDefault(r => r.Name == "RemoveCountRole");
            Assert.NotNull(countRole);
            Assert.Equal(0, countRole.UserCount);
        }
    }
}
