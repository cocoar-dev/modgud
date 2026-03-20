using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class TwoFactorAuthTests : IAsyncLifetime
{
    private readonly CocoarAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TwoFactorAuthTests(SharedPostgresFixture fixture)
    {
        _factory = new CocoarAuthWebApplicationFactory(fixture);
        _client = _factory.CreateClientWithCookies();
    }

    public Task InitializeAsync() => _factory.CleanDatabaseAsync();

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    #region 2FA Status Tests

    [Fact]
    public async Task GetTwoFactorStatus_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/system/api/auth/2fa/status");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTwoFactorStatus_WithAuthentication_ReturnsStatus()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("2fastatususer", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.GetAsync("/system/api/auth/2fa/status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<TwoFactorStatusDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.IsEnabled);
        Assert.False(result.HasAuthenticator);
        Assert.Equal(0, result.RecoveryCodesRemaining);
    }

    #endregion

    #region 2FA Setup Tests

    [Fact]
    public async Task SetupTwoFactor_WithAuthentication_ReturnsSetupInfo()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("2fasetupuser", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/system/api/auth/2fa/setup", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<TwoFactorSetupDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.NotEmpty(result.SharedKey);
        Assert.Contains("otpauth://totp/", result.AuthenticatorUri);
    }

    #endregion

    #region Enable/Disable 2FA Tests

    [Fact]
    public async Task EnableTwoFactor_WithValidCode_Succeeds()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("enable2fauser", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Setup 2FA and get the key
        var setupResponse = await _client.PostAsync("/system/api/auth/2fa/setup", null);
        var setupResult = await setupResponse.ReadFromJsonAsync<TwoFactorSetupDto>(_factory.JsonOptions);

        // Generate a valid TOTP code
        var key = setupResult!.SharedKey.Replace(" ", "").ToUpperInvariant();
        var totp = new Totp(Base32Encoding.ToBytes(key));
        var code = totp.ComputeTotp();

        // Act
        var enableDto = new { Code = code };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/enable", enableDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RecoveryCodesDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(10, result.Codes.Count); // Default recovery code count

        // Verify 2FA is now enabled
        var statusResponse = await _client.GetAsync("/system/api/auth/2fa/status");
        var status = await statusResponse.ReadFromJsonAsync<TwoFactorStatusDto>(_factory.JsonOptions);
        Assert.True(status?.IsEnabled);
    }

    [Fact]
    public async Task EnableTwoFactor_WithInvalidCode_ReturnsBadRequest()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync("invalidcode2fa", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Setup 2FA
        await _client.PostAsync("/system/api/auth/2fa/setup", null);

        // Act - try to enable with invalid code
        var enableDto = new { Code = "000000" };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/enable", enableDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisableTwoFactor_WithValidCode_Succeeds()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("disable2fauser", password);

        // Login requires 2FA now - first do password login
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Complete 2FA login
        var key = await GetAuthenticatorKeyAsync(user);
        var totp = new Totp(Base32Encoding.ToBytes(key!));
        await _client.PostAsJsonAsync("/system/api/auth/2fa/login", new { Code = totp.ComputeTotp(), RememberMachine = false }, _factory.JsonOptions);

        // Generate a new code for disabling (previous code may have expired)
        var disableCode = totp.ComputeTotp();

        // Act
        var disableDto = new { Code = disableCode };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/disable", disableDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify 2FA is now disabled
        var statusResponse = await _client.GetAsync("/system/api/auth/2fa/status");
        var status = await statusResponse.ReadFromJsonAsync<TwoFactorStatusDto>(_factory.JsonOptions);
        Assert.False(status?.IsEnabled);
    }

    #endregion

    #region 2FA Login Tests

    [Fact]
    public async Task Login_WithTwoFactorEnabled_RequiresTwoFactor()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("login2fauser", password);

        // Logout to clear session
        await _client.PostAsync("/system/api/auth/logout", null);

        // Act - Try to login
        var loginResponse = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var result = await loginResponse.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.True(result.RequiresTwoFactor);
    }

    [Fact]
    public async Task TwoFactorLogin_WithValidCode_Succeeds()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("2faloginvalid", password);

        // Logout and login to trigger 2FA
        await _client.PostAsync("/system/api/auth/logout", null);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Get the authenticator key and generate code
        var key = await GetAuthenticatorKeyAsync(user);
        var totp = new Totp(Base32Encoding.ToBytes(key!));
        var code = totp.ComputeTotp();

        // Act
        var twoFactorDto = new { Code = code, RememberMachine = false };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/login", twoFactorDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);

        // Verify we're now authenticated
        var meResponse = await _client.GetAsync("/system/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task TwoFactorLogin_WithInvalidCode_Fails()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("2faloginbad", password);

        // Logout and login to trigger 2FA
        await _client.PostAsync("/system/api/auth/logout", null);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var twoFactorDto = new { Code = "000000", RememberMachine = false };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/login", twoFactorDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Contains("Invalid", result.ErrorMessage);
    }

    #endregion

    #region Recovery Code Tests

    [Fact]
    public async Task GenerateRecoveryCodes_WithTwoFactorEnabled_ReturnsNewCodes()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("recoverycodes", password);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Complete 2FA login
        var key = await GetAuthenticatorKeyAsync(user);
        var totp = new Totp(Base32Encoding.ToBytes(key!));
        await _client.PostAsJsonAsync("/system/api/auth/2fa/login", new { Code = totp.ComputeTotp(), RememberMachine = false }, _factory.JsonOptions);

        // Act
        var response = await _client.PostAsync("/system/api/auth/2fa/recovery-codes", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<RecoveryCodesDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(10, result.Codes.Count);
    }

    [Fact]
    public async Task RecoveryCodeLogin_WithValidCode_Succeeds()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("recoverylogin", password);
        var recoveryCodes = await GetRecoveryCodesAsync(user);

        // Logout and login to trigger 2FA
        await _client.PostAsync("/system/api/auth/logout", null);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act
        var recoveryDto = new { Code = recoveryCodes.First() };
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/recovery-login", recoveryDto, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RecoveryCodeLogin_WithUsedCode_Fails()
    {
        // Arrange
        var password = "Test123!@#";
        var user = await Enable2FAForUserAsync("usedrecovery", password);
        var recoveryCodes = await GetRecoveryCodesAsync(user);
        var codeToUse = recoveryCodes.First();

        // Use the code once
        await _client.PostAsync("/system/api/auth/logout", null);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        await _client.PostAsJsonAsync("/system/api/auth/2fa/recovery-login", new { Code = codeToUse }, _factory.JsonOptions);

        // Logout and try again
        await _client.PostAsync("/system/api/auth/logout", null);
        await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);

        // Act - Try to use the same code again
        var response = await _client.PostAsJsonAsync("/system/api/auth/2fa/recovery-login", new { Code = codeToUse }, _factory.JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
    }

    #endregion

    #region Helper Methods

    private async Task<ApplicationUser> Enable2FAForUserAsync(string userName, string password)
    {
        var user = await _factory.CreateTestUserAsync(userName, password);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Generate and set authenticator key
        await userManager.ResetAuthenticatorKeyAsync(user);
        var key = await userManager.GetAuthenticatorKeyAsync(user);

        // Enable 2FA
        await userManager.SetTwoFactorEnabledAsync(user, true);

        // Generate recovery codes
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

        return user;
    }

    private async Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.GetAuthenticatorKeyAsync(user);
    }

    private async Task<IEnumerable<string>> GetRecoveryCodesAsync(ApplicationUser user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Generate new codes and return them
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return codes ?? [];
    }

    #endregion
}
