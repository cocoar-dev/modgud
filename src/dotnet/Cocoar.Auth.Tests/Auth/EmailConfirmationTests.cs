using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class EmailConfirmationTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public EmailConfirmationTests(SharedPostgresFixture fixture)
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
    public async Task ConfirmEmail_WithValidToken_Succeeds()
    {
        // Arrange - Register a user
        var registerDto = new RegisterDto
        {
            UserName = "confirmtest",
            Email = "confirmtest@test.com",
            Password = "Confirm123!@#"
        };

        var registerResponse = await _client.PostAsJsonAsync("/system/api/auth/register", registerDto, _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        var registerResult = await registerResponse.Content.ReadFromJsonAsync<RegisterResultDto>(_factory.JsonOptions);
        Assert.NotNull(registerResult);
        Assert.True(registerResult.Succeeded);

        // Get the confirmation link from the email
        var emailSender = _factory.GetMockEmailSender();
        var sentEmail = emailSender.SentEmails.First(e => e.To == "confirmtest@test.com");

        // Extract the confirmation URL from the email body
        var match = Regex.Match(sentEmail.Body, @"href=""([^""]+)""");
        Assert.True(match.Success, "Could not find confirmation link in email");

        var confirmationUrl = match.Groups[1].Value;
        // Convert absolute URL to relative for the test client
        var uri = new Uri(confirmationUrl);
        var relativeUrl = uri.PathAndQuery;

        // Act - Confirm the email
        var confirmResponse = await _client.GetAsync(relativeUrl);

        // Assert
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_WithInvalidToken_ReturnsBadRequest()
    {
        // Arrange - Register a user
        var registerDto = new RegisterDto
        {
            UserName = "invalidtoken",
            Email = "invalidtoken@test.com",
            Password = "Invalid123!@#"
        };

        var registerResponse = await _client.PostAsJsonAsync("/system/api/auth/register", registerDto, _factory.JsonOptions);
        var registerResult = await registerResponse.Content.ReadFromJsonAsync<RegisterResultDto>(_factory.JsonOptions);

        // Act - Try to confirm with invalid token
        var confirmResponse = await _client.GetAsync($"/system/api/auth/confirm-email?userId={registerResult!.UserId}&token=invalidtoken");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_WithInvalidUserId_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/system/api/auth/confirm-email?userId={Guid.NewGuid()}&token=sometoken");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_AlreadyConfirmed_ReturnsBadRequest()
    {
        // Arrange - Register and confirm
        var registerDto = new RegisterDto
        {
            UserName = "alreadyconfirmed",
            Email = "alreadyconfirmed@test.com",
            Password = "Already123!@#"
        };

        await _client.PostAsJsonAsync("/system/api/auth/register", registerDto, _factory.JsonOptions);

        var emailSender = _factory.GetMockEmailSender();
        var sentEmail = emailSender.SentEmails.First(e => e.To == "alreadyconfirmed@test.com");
        var match = Regex.Match(sentEmail.Body, @"href=""([^""]+)""");
        var uri = new Uri(match.Groups[1].Value);
        var relativeUrl = uri.PathAndQuery;

        // Confirm once
        await _client.GetAsync(relativeUrl);

        // Act - Try to confirm again
        var secondConfirmResponse = await _client.GetAsync(relativeUrl);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, secondConfirmResponse.StatusCode);
    }

    [Fact]
    public async Task ResendConfirmation_ForExistingUser_SendsEmail()
    {
        // Arrange - Register a user
        var registerDto = new RegisterDto
        {
            UserName = "resendtest",
            Email = "resendtest@test.com",
            Password = "Resend123!@#"
        };

        await _client.PostAsJsonAsync("/system/api/auth/register", registerDto, _factory.JsonOptions);

        var emailSender = _factory.GetMockEmailSender();
        var initialCount = emailSender.SentEmails.Count;

        // Act
        var resendDto = new ResendConfirmationDto { Email = "resendtest@test.com" };
        var response = await _client.PostAsJsonAsync("/system/api/auth/resend-confirmation", resendDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(initialCount + 1, emailSender.SentEmails.Count);
    }

    [Fact]
    public async Task ResendConfirmation_ForNonExistentUser_ReturnsOk()
    {
        // Act - Resend for non-existent user (should not reveal user doesn't exist)
        var resendDto = new ResendConfirmationDto { Email = "nonexistent@test.com" };
        var response = await _client.PostAsJsonAsync("/system/api/auth/resend-confirmation", resendDto, _factory.JsonOptions);

        // Assert - Should return OK to not reveal user existence
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
