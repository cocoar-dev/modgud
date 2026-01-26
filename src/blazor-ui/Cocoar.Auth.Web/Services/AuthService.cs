using System.Net.Http.Json;
using Cocoar.Auth.Web.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Cocoar.Auth.Web.Services;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request);
    Task LogoutAsync();
    Task<UserDto?> RegisterAsync(RegisterRequest request);
    Task<bool> ForgotPasswordAsync(ForgotPasswordRequest request);
    Task<bool> ResetPasswordAsync(ResetPasswordRequest request);
    Task<bool> ConfirmEmailAsync(string userId, string token);
    Task<bool> ResendConfirmationAsync(string email);
    Task<CurrentUserInfo?> GetCurrentUserAsync();
    Task<UserProfileDto?> GetProfileAsync();
    Task<UserProfileDto?> UpdateProfileAsync(UpdateProfileRequest request);
    Task<bool> ChangePasswordAsync(ChangePasswordRequest request);

    // Two-Factor Authentication
    Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync();
    Task<TwoFactorSetupResponse?> SetupTwoFactorAsync();
    Task<bool> EnableTwoFactorAsync(string code);
    Task<bool> DisableTwoFactorAsync(string code);
    Task<RecoveryCodesResponse?> GenerateRecoveryCodesAsync();
    Task<LoginResponse> TwoFactorLoginAsync(string code, bool rememberMachine);
    Task<LoginResponse> RecoveryCodeLoginAsync(string code);

    // Sessions
    Task<SessionListResponse?> GetSessionsAsync();
    Task<bool> RevokeSessionAsync(string sessionId);
    Task<bool> RevokeAllSessionsAsync();

    // GDPR
    Task<UserDataExportResponse?> ExportDataAsync();
    Task<DeletionRequestResponse?> RequestDeletionAsync(string password, string? reason);
    Task<bool> CancelDeletionAsync();
    Task<DeletionStatusResponse?> GetDeletionStatusAsync();
}

public class AuthService : IAuthService
{
    private readonly HttpClient _http;

    public AuthService(HttpClient http)
    {
        _http = http;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
            {
                // Check if response indicates 2FA is required
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(content))
                {
                    var loginResult = await response.Content.ReadFromJsonAsync<LoginApiResponse>();
                    if (loginResult?.RequiresTwoFactor == true)
                    {
                        return new LoginResponse(false, RequiresTwoFactor: true);
                    }
                }
                return new LoginResponse(true);
            }

            // Check for 2FA required in error response (some APIs return this as a specific status)
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                return new LoginResponse(false, RequiresTwoFactor: true);
            }

            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            return new LoginResponse(false, Error: error?.Title ?? "Login failed");
        }
        catch (Exception ex)
        {
            return new LoginResponse(false, Error: ex.Message);
        }
    }

    private record LoginApiResponse(bool? RequiresTwoFactor = null);

    public async Task LogoutAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        await _http.SendAsync(request);
    }

    public async Task<UserDto?> RegisterAsync(RegisterRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/register", request);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<UserDto>();
        }
        return null;
    }

    public async Task<bool> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/forgot-password", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResetPasswordAsync(ResetPasswordRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/reset-password", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ConfirmEmailAsync(string userId, string token)
    {
        var response = await _http.GetAsync($"/api/auth/confirm-email?userId={userId}&token={Uri.EscapeDataString(token)}");
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResendConfirmationAsync(string email)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/resend-confirmation", new { Email = email });
        return response.IsSuccessStatusCode;
    }

    public async Task<CurrentUserInfo?> GetCurrentUserAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                var user = await response.Content.ReadFromJsonAsync<CurrentUserInfo>();
                if (user != null)
                {
                    user.IsAuthenticated = true;
                }
                return user;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UserProfileDto?> GetProfileAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/profile");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            
            var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserProfileDto>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UserProfileDto?> UpdateProfileAsync(UpdateProfileRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Put, "/api/auth/profile")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        
        var response = await _http.SendAsync(httpRequest);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<UserProfileDto>();
        }
        return null;
    }

    public async Task<bool> ChangePasswordAsync(ChangePasswordRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TWO-FACTOR AUTHENTICATION
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/2fa/status");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TwoFactorStatusResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<TwoFactorSetupResponse?> SetupTwoFactorAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/setup");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TwoFactorSetupResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> EnableTwoFactorAsync(string code)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/enable")
        {
            Content = JsonContent.Create(new EnableTwoFactorRequest(code))
        };
        httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DisableTwoFactorAsync(string code)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/disable")
        {
            Content = JsonContent.Create(new DisableTwoFactorRequest(code))
        };
        httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(httpRequest);
        return response.IsSuccessStatusCode;
    }

    public async Task<RecoveryCodesResponse?> GenerateRecoveryCodesAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/recovery-codes");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<RecoveryCodesResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LoginResponse> TwoFactorLoginAsync(string code, bool rememberMachine)
    {
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/login")
            {
                Content = JsonContent.Create(new TwoFactorLoginRequest(code, rememberMachine))
            };
            httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(httpRequest);
            if (response.IsSuccessStatusCode)
            {
                return new LoginResponse(true);
            }

            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            return new LoginResponse(false, Error: error?.Title ?? "Invalid verification code");
        }
        catch (Exception ex)
        {
            return new LoginResponse(false, Error: ex.Message);
        }
    }

    public async Task<LoginResponse> RecoveryCodeLoginAsync(string code)
    {
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/recovery-login")
            {
                Content = JsonContent.Create(new RecoveryCodeLoginRequest(code))
            };
            httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(httpRequest);
            if (response.IsSuccessStatusCode)
            {
                return new LoginResponse(true);
            }

            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            return new LoginResponse(false, Error: error?.Title ?? "Invalid recovery code");
        }
        catch (Exception ex)
        {
            return new LoginResponse(false, Error: ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SESSIONS
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<SessionListResponse?> GetSessionsAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/sessions");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var sessions = await response.Content.ReadFromJsonAsync<List<SessionDto>>();
                return new SessionListResponse(sessions ?? []);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RevokeSessionAsync(string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/sessions/{sessionId}");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RevokeAllSessionsAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/sessions");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GDPR
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<UserDataExportResponse?> ExportDataAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/export-data");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserDataExportResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DeletionRequestResponse?> RequestDeletionAsync(string password, string? reason)
    {
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/delete-account")
            {
                Content = JsonContent.Create(new RequestDeletionRequest(password, reason))
            };
            httpRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(httpRequest);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DeletionRequestResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CancelDeletionAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/cancel-deletion");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeletionStatusResponse?> GetDeletionStatusAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/deletion-status");
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DeletionStatusResponse>();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
