using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class PasswordResetTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public PasswordResetTests(SharedPostgresFixture fixture)
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
    public async Task ForgotPassword_ForExistingUser_SendsEmail()
    {
        // Arrange
        await _factory.CreateTestUserAsync("resetuser", email: "resetuser@test.com");
        var emailSender = _factory.GetMockEmailSender();
        emailSender.Clear();

        // Act
        var dto = new ForgotPasswordDto { Email = "resetuser@test.com" };
        var response = await _client.PostAsJsonAsync("/system/api/auth/forgot-password", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(emailSender.SentEmails);
        Assert.Contains("Reset", emailSender.SentEmails[0].Subject);
    }

    [Fact]
    public async Task ForgotPassword_ForNonExistentUser_ReturnsOk()
    {
        // Arrange
        var emailSender = _factory.GetMockEmailSender();
        emailSender.Clear();

        // Act - Should not reveal user doesn't exist
        var dto = new ForgotPasswordDto { Email = "nonexistent@test.com" };
        var response = await _client.PostAsJsonAsync("/system/api/auth/forgot-password", dto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(emailSender.SentEmails); // No email should be sent
    }

    [Fact]
    public async Task ResetPassword_WithValidToken_Succeeds()
    {
        // Arrange - Create user and request password reset
        await _factory.CreateTestUserAsync("resetvalid", email: "resetvalid@test.com");

        var forgotDto = new ForgotPasswordDto { Email = "resetvalid@test.com" };
        await _client.PostAsJsonAsync("/system/api/auth/forgot-password", forgotDto, _factory.JsonOptions);

        // Extract token from email
        var emailSender = _factory.GetMockEmailSender();
        var sentEmail = emailSender.SentEmails.First(e => e.To == "resetvalid@test.com");
        var match = Regex.Match(sentEmail.Body, @"href=""[^""]*token=([^""&]+)""");
        Assert.True(match.Success, "Could not find token in reset email");
        var token = match.Groups[1].Value;

        // Act
        var resetDto = new ResetPasswordRequestDto
        {
            Email = "resetvalid@test.com",
            Token = token,
            NewPassword = "NewPassword123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/reset-password", resetDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify new password works
        var loginDto = new LoginDto
        {
            UserName = "resetvalid",
            Password = "NewPassword123!@#"
        };
        var loginResponse = await _client.PostAsJsonAsync("/system/api/auth/login", loginDto, _factory.JsonOptions);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.True(loginResult!.Succeeded);
    }

    [Fact]
    public async Task ResetPassword_WithInvalidToken_ReturnsBadRequest()
    {
        // Arrange
        await _factory.CreateTestUserAsync("resetinvalid", email: "resetinvalid@test.com");

        // Act
        var resetDto = new ResetPasswordRequestDto
        {
            Email = "resetinvalid@test.com",
            Token = "invalidtoken",
            NewPassword = "NewPassword123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/reset-password", resetDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithNonExistentUser_ReturnsNotFound()
    {
        // Act
        var resetDto = new ResetPasswordRequestDto
        {
            Email = "nonexistent@test.com",
            Token = "sometoken",
            NewPassword = "NewPassword123!@#"
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/reset-password", resetDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithWeakPassword_ReturnsBadRequest()
    {
        // Arrange
        await _factory.CreateTestUserAsync("weakpassword", email: "weakpassword@test.com");

        var forgotDto = new ForgotPasswordDto { Email = "weakpassword@test.com" };
        await _client.PostAsJsonAsync("/system/api/auth/forgot-password", forgotDto, _factory.JsonOptions);

        var emailSender = _factory.GetMockEmailSender();
        var sentEmail = emailSender.SentEmails.First(e => e.To == "weakpassword@test.com");
        var match = Regex.Match(sentEmail.Body, @"href=""[^""]*token=([^""&]+)""");
        var token = match.Groups[1].Value;

        // Act
        var resetDto = new ResetPasswordRequestDto
        {
            Email = "weakpassword@test.com",
            Token = token,
            NewPassword = "weak" // Too weak
        };
        var response = await _client.PostAsJsonAsync("/system/api/auth/reset-password", resetDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
