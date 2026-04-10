using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Projections;

/// <summary>
/// Tests for UserDetailsProjection async projection functionality.
/// These tests verify that the denormalized user view stays consistent with role changes.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.GDPR)]
public class UserDetailsProjectionTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public UserDetailsProjectionTests(SharedPostgresFixture fixture)
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
    public async Task GetUser_ReturnsUserWithDenormalizedRoleInfo()
    {
        // Arrange
        await LoginAsAdminAsync();

        // Create a role and user with that role
        var role = await _factory.CreateTestRoleAsync("Manager", "Manager role description");
        var user = await _factory.CreateTestUserAsync("testuser");

        // Assign role to user
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "Manager");
        }

        var userShortGuid = new ShortGuid(user.Id);

        // Act
        var response = await _client.GetAsync($"/api/admin/users/{userShortGuid}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("testuser", result.UserName);

        // Verify denormalized role data is present
        Assert.Single(result.Roles);
        var roleShortGuid = result.Roles[0];
        Assert.Equal(role.Id, roleShortGuid.Guid);
    }

    [Fact]
    public async Task WhenRoleNameChanges_AllUsersWithRoleAreUpdated()
    {
        // Arrange
        await LoginAsAdminAsync();

        // Create a role
        var role = await _factory.CreateTestRoleAsync("Developer", "Dev role");
        var roleShortGuid = new ShortGuid(role.Id);

        // Create users and assign the role
        var user1 = await _factory.CreateTestUserAsync("user1");
        var user2 = await _factory.CreateTestUserAsync("user2");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser1 = await userManager.FindByIdAsync(user1.Id.ToString());
            var appUser2 = await userManager.FindByIdAsync(user2.Id.ToString());
            await userManager.AddToRoleAsync(appUser1!, "Developer");
            await userManager.AddToRoleAsync(appUser2!, "Developer");
        }

        // Update the role name
        var updateDto = new UpdateRoleDto
        {
            Name = new Optional<string>("Senior Developer")
        };
        var updateResponse = await _client.PatchAsJsonAsync(
            $"/api/admin/roles/{roleShortGuid}",
            updateDto,
            _factory.JsonOptions);
        Assert.Equal(System.Net.HttpStatusCode.OK, updateResponse.StatusCode);

        // Act - Query users via UserDetailsRepository
        using (var scope = _factory.Services.CreateScope())
        {
            var userDetailsRepo = scope.ServiceProvider.GetRequiredService<IUserDetailsRepository>();

            var userDetails1 = await userDetailsRepo.GetByIdAsync(user1.Id);
            var userDetails2 = await userDetailsRepo.GetByIdAsync(user2.Id);

            // Assert - Both users should have the updated role name
            Assert.NotNull(userDetails1);
            Assert.NotNull(userDetails2);

            var role1 = userDetails1.Roles.FirstOrDefault(r => r.Id == role.Id);
            var role2 = userDetails2.Roles.FirstOrDefault(r => r.Id == role.Id);

            Assert.NotNull(role1);
            Assert.NotNull(role2);
            Assert.Equal("Senior Developer", role1.Name);
            Assert.Equal("Senior Developer", role2.Name);
        }
    }

    [Fact]
    public async Task WhenRoleDescriptionChanges_AllUsersWithRoleAreUpdated()
    {
        // Arrange
        await LoginAsAdminAsync();

        var role = await _factory.CreateTestRoleAsync("Tester", "Original description");
        var roleShortGuid = new ShortGuid(role.Id);

        var user = await _factory.CreateTestUserAsync("tester1");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "Tester");
        }

        // Update the role description
        var updateDto = new UpdateRoleDto
        {
            Description = new Optional<string?>("Updated tester description")
        };
        var updateResponse = await _client.PatchAsJsonAsync(
            $"/api/admin/roles/{roleShortGuid}",
            updateDto,
            _factory.JsonOptions);
        Assert.Equal(System.Net.HttpStatusCode.OK, updateResponse.StatusCode);

        // Act
        using (var scope = _factory.Services.CreateScope())
        {
            var userDetailsRepo = scope.ServiceProvider.GetRequiredService<IUserDetailsRepository>();
            var userDetails = await userDetailsRepo.GetByIdAsync(user.Id);

            // Assert
            Assert.NotNull(userDetails);
            var roleInfo = userDetails.Roles.FirstOrDefault(r => r.Id == role.Id);
            Assert.NotNull(roleInfo);
            Assert.Equal("Updated tester description", roleInfo.Description);
        }
    }

    [Fact]
    public async Task DeletedUsers_AreFilteredFromQueries()
    {
        // Arrange
        await LoginAsAdminAsync();

        var user1 = await _factory.CreateTestUserAsync("activeuser");
        var user2 = await _factory.CreateTestUserAsync("deleteduser");
        var user2ShortGuid = new ShortGuid(user2.Id);

        // Delete user2
        var deleteResponse = await _client.DeleteAsync($"/api/admin/users/{user2ShortGuid}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Act - Query all users
        var response = await _client.GetAsync("/api/admin/users");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);

        // Assert - Deleted user should not appear in results
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, u => u.UserName == "deleteduser");
        Assert.Contains(result.Items, u => u.UserName == "activeuser");
    }

    [Fact]
    public async Task GetUserById_ForDeletedUser_ReturnsNotFound()
    {
        // Arrange
        await LoginAsAdminAsync();

        var user = await _factory.CreateTestUserAsync("tobedeleted");
        var userShortGuid = new ShortGuid(user.Id);

        // Delete the user
        var deleteResponse = await _client.DeleteAsync($"/api/admin/users/{userShortGuid}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Act - Try to get deleted user
        var getResponse = await _client.GetAsync($"/api/admin/users/{userShortGuid}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetUsersList_ReturnsDenormalizedData()
    {
        // Arrange
        await LoginAsAdminAsync();

        var role = await _factory.CreateTestRoleAsync("Viewer", "Read-only access");
        var user = await _factory.CreateTestUserAsync("viewer1");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Domain.Entities.ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.AddToRoleAsync(appUser!, "Viewer");
        }

        // Act
        var response = await _client.GetAsync("/api/admin/users");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<UserListDto>(_factory.JsonOptions);

        // Assert
        Assert.NotNull(result);
        var viewer = result.Items.FirstOrDefault(u => u.UserName == "viewer1");
        Assert.NotNull(viewer);
        Assert.Single(viewer.Roles);
        Assert.Equal(role.Id, viewer.Roles[0].Guid);
    }
}
