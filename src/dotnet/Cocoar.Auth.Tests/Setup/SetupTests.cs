using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Api.Controllers;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Setup;

[Collection(IntegrationTestCollection.Name)]
public class SetupTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public SetupTests(SharedPostgresFixture fixture)
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

    [Fact]
    public async Task GetStatus_WhenNoAdminExists_ReturnsNeedsSetupTrue()
    {
        // Act
        var response = await _client.GetAsync("/system/api/setup/status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<SetupStatusDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.NeedsSetup);
    }

    [Fact]
    public async Task GetStatus_WhenAdminExists_ReturnsNeedsSetupFalse()
    {
        // Arrange
        await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);

        // Act
        var response = await _client.GetAsync("/system/api/setup/status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<SetupStatusDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.NeedsSetup);
    }

    [Fact]
    public async Task CreateAdmin_WhenNoAdminExists_CreatesAdminSuccessfully()
    {
        // Arrange
        var request = new CreateAdminDto
        {
            UserName = "newadmin",
            Password = "NewAdmin123!@#",
            Email = "newadmin@test.com",
            FirstName = "New",
            LastName = "Admin"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/setup/create-admin", request, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<SetupResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Success);

        // Verify admin can now login
        var loginResponse = await _client.LoginAsync("newadmin", "NewAdmin123!@#", _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Verify setup is no longer needed
        var statusResponse = await _client.GetAsync("/system/api/setup/status");
        var statusResult = await statusResponse.ReadFromJsonAsync<SetupStatusDto>(_factory.JsonOptions);
        Assert.NotNull(statusResult);
        Assert.False(statusResult.NeedsSetup);
    }

    [Fact]
    public async Task CreateAdmin_WhenAdminAlreadyExists_ReturnsNotFound()
    {
        // Arrange
        await _factory.CreateTestUserAsync("existingadmin", "Admin123!@#", isAdmin: true);

        var request = new CreateAdminDto
        {
            UserName = "anotheradmin",
            Password = "AnotherAdmin123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/setup/create-admin", request, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_WithWeakPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAdminDto
        {
            UserName = "weakadmin",
            Password = "weak"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/setup/create-admin", request, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_WithMissingUserName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAdminDto
        {
            UserName = "",
            Password = "ValidPassword123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/setup/create-admin", request, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_WithDuplicateUserName_ReturnsBadRequest()
    {
        // Arrange - create a non-admin user first
        await _factory.CreateTestUserAsync("takenname", "Test123!@#", isAdmin: false);

        var request = new CreateAdminDto
        {
            UserName = "takenname",
            Password = "ValidPassword123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/system/api/setup/create-admin", request, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
