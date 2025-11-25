using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

public class RegistrationTests : IClassFixture<CocoarAuthWebApplicationFactory>, IAsyncLifetime
{
    private readonly CocoarAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegistrationTests(CocoarAuthWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClientWithCookies();
    }

    public Task InitializeAsync() => _factory.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_WithValidData_ReturnsSuccessAndSendsEmail()
    {
        // Arrange
        var emailSender = _factory.GetMockEmailSender();
        emailSender.Clear();

        var dto = new RegisterDto
        {
            UserName = "newuser",
            Email = "newuser@test.com",
            Password = "NewUser123!@#",
            FirstName = "New",
            LastName = "User"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RegisterResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.True(result.RequiresEmailConfirmation);
        Assert.NotNull(result.UserId);

        // Verify email was sent
        var sentEmails = emailSender.SentEmails;
        Assert.Single(sentEmails);
        Assert.Equal("newuser@test.com", sentEmails[0].To);
        Assert.Contains("Confirm", sentEmails[0].Subject);
    }

    [Fact]
    public async Task Register_WithDuplicateUsername_ReturnsConflict()
    {
        // Arrange
        await _factory.CreateTestUserAsync("existinguser");

        var dto = new RegisterDto
        {
            UserName = "existinguser",
            Email = "different@test.com",
            Password = "NewUser123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        // Arrange
        await _factory.CreateTestUserAsync("user1", email: "duplicate@test.com");

        var dto = new RegisterDto
        {
            UserName = "user2",
            Email = "duplicate@test.com",
            Password = "NewUser123!@#"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithWeakPassword_ReturnsBadRequest()
    {
        // Arrange
        var dto = new RegisterDto
        {
            UserName = "newuser",
            Email = "newuser@test.com",
            Password = "weak" // Too weak
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RegisterResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Register_ThenLogin_Succeeds()
    {
        // Arrange
        var registerDto = new RegisterDto
        {
            UserName = "logintest",
            Email = "logintest@test.com",
            Password = "LoginTest123!@#"
        };

        // Register
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", registerDto, _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        // Act - Login
        var loginDto = new LoginDto
        {
            UserName = "logintest",
            Password = "LoginTest123!@#"
        };
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var result = await loginResponse.Content.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
    }
}
