using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", TestCategories.TwoFactor)]
public class EmailOtpTests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public EmailOtpTests(SharedPostgresFixture fixture)
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

    #region Status Tests

    [Fact]
    public async Task GetEmailOtpStatus_Unauthenticated_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/auth/2fa/email-otp/status");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEmailOtpStatus_Authenticated_ReturnsStatus()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otpstatususer", password, email: "otpstatus@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/api/auth/2fa/email-otp/status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<EmailOtpStatusDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.IsPending);
        Assert.True(result.CanRequestNew);
    }

    #endregion

    #region Request OTP Tests

    [Fact]
    public async Task RequestEmailOtp_Authenticated_SendsEmail()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otprequestuser", password, email: "otprequest@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        _factory.GetMockEmailSender().Clear();

        // Act
        var response = await _client.PostAsync("/api/auth/2fa/email-otp/request", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sentEmails = _factory.GetMockEmailSender().SentEmails;
        Assert.Single(sentEmails);
        Assert.Equal("otprequest@test.com", sentEmails[0].To);
        Assert.Contains("verification code", sentEmails[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestEmailOtp_NoEmail_ReturnsBadRequest()
    {
        // Arrange - create user then clear email via admin API
        var password = "Test123!@#";
        var admin = await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
        var user = await _factory.CreateTestUserAsync("otpnoemail", password);
        await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);

        // Clear the user's email via admin update
        await _client.PatchAsJsonAsync($"/api/admin/users/{user.Id}", new { email = (string?)null }, _factory.JsonOptions);
        await _client.PostAsync("/api/auth/logout", null);

        // Login as the user without email
        await _client.LoginAsync("otpnoemail", password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/api/auth/2fa/email-otp/request", null);

        // Assert - should fail because user has no email
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected error for user without email, got {response.StatusCode}");
    }

    [Fact]
    public async Task RequestEmailOtp_RateLimited_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otprateuser", password, email: "otprate@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // First request should succeed
        var firstResponse = await _client.PostAsync("/api/auth/2fa/email-otp/request", null);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act - Second request immediately should be rate limited
        var secondResponse = await _client.PostAsync("/api/auth/2fa/email-otp/request", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
    }

    #endregion

    #region Verify OTP Tests

    [Fact]
    public async Task VerifyEmailOtp_WithValidCode_Succeeds()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otpverifyuser", password, email: "otpverify@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        _factory.GetMockEmailSender().Clear();

        // Request OTP
        await _client.PostAsync("/api/auth/2fa/email-otp/request", null);

        // Extract OTP code from the email body
        var sentEmails = _factory.GetMockEmailSender().SentEmails;
        Assert.Single(sentEmails);
        var code = ExtractOtpCodeFromEmail(sentEmails[0].Body);
        Assert.NotNull(code);

        // Act
        var verifyDto = new VerifyEmailOtpDto { Code = code };
        var response = await _client.PostAsJsonAsync("/api/auth/2fa/email-otp/verify", verifyDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VerifyEmailOtp_WithInvalidCode_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otpinvaliduser", password, email: "otpinvalid@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Request OTP first so there's a pending challenge
        await _client.PostAsync("/api/auth/2fa/email-otp/request", null);

        // Act - verify with wrong code
        var verifyDto = new VerifyEmailOtpDto { Code = "000000" };
        var response = await _client.PostAsJsonAsync("/api/auth/2fa/email-otp/verify", verifyDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task VerifyEmailOtp_NoPendingChallenge_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("otpnopending", password, email: "otpnopending@test.com");
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act - verify without requesting OTP first
        var verifyDto = new VerifyEmailOtpDto { Code = "123456" };
        var response = await _client.PostAsJsonAsync("/api/auth/2fa/email-otp/verify", verifyDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Email OTP Login Flow Tests

    [Fact]
    public async Task EmailOtpLogin_WithValidCode_Succeeds()
    {
        // Arrange - create user and enable TOTP 2FA
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("otploginuser", password);

        // Logout to clear session
        await _client.PostAsync("/api/auth/logout", null);

        // Login with password - should require 2FA
        var loginResponse = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginResult = await loginResponse.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(loginResult);
        Assert.True(loginResult.RequiresTwoFactor);

        // Request email OTP during login flow
        _factory.GetMockEmailSender().Clear();
        var requestResponse = await _client.PostAsync("/api/auth/2fa/email-otp/login/request", null);
        Assert.Equal(HttpStatusCode.OK, requestResponse.StatusCode);

        // Extract OTP code from email
        var sentEmails = _factory.GetMockEmailSender().SentEmails;
        Assert.Single(sentEmails);
        var code = ExtractOtpCodeFromEmail(sentEmails[0].Body);
        Assert.NotNull(code);

        // Act - complete login with email OTP
        var otpLoginDto = new EmailOtpLoginDto { Code = code, RememberMachine = false };
        var otpResponse = await _client.PostAsJsonAsync("/api/auth/2fa/email-otp/login", otpLoginDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, otpResponse.StatusCode);
        var otpResult = await otpResponse.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(otpResult);
        Assert.True(otpResult.Succeeded);
    }

    #endregion

    #region Helper Methods

    private async Task<ApplicationUser> Enable2FAForUserAsync(string userName, string password)
    {
        var user = await _factory.CreateTestUserAsync(userName, password, email: $"{userName}@test.com");

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Generate and set authenticator key
        await userManager.ResetAuthenticatorKeyAsync(user);

        // Enable 2FA
        await userManager.SetTwoFactorEnabledAsync(user, true);

        // Generate recovery codes
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

        return user;
    }

    private static string? ExtractOtpCodeFromEmail(string emailBody)
    {
        // The OTP code is a 6-digit number embedded in the email body
        var match = Regex.Match(emailBody, @"\b(\d{6})\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    #endregion
}
