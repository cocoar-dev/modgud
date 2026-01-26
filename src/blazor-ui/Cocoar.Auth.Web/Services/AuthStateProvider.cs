using System.Security.Claims;
using Cocoar.Auth.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cocoar.Auth.Web.Services;

/// <summary>
/// Authentication state provider for Blazor that integrates with the auth API.
/// </summary>
public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;

    public CurrentUserInfo? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser?.IsAuthenticated ?? false;
    public bool IsAdmin => CurrentUser?.Roles.Contains("Admin") ?? false;

    // Track pending 2FA state
    public bool RequiresTwoFactor { get; private set; }
    public string? PendingTwoFactorUserName { get; private set; }

    public event Action? OnAuthStateChanged;

    public AuthStateProvider(IAuthService authService)
    {
        _authService = authService;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await RefreshAuthStateAsync();

        if (CurrentUser?.IsAuthenticated == true)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, CurrentUser.UserName ?? ""),
                new(ClaimTypes.Email, CurrentUser.Email ?? "")
            };

            foreach (var role in CurrentUser.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var identity = new ClaimsIdentity(claims, "cookie");
            var user = new ClaimsPrincipal(identity);
            return new AuthenticationState(user);
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    public async Task InitializeAsync()
    {
        await RefreshAuthStateAsync();
    }

    public async Task RefreshAuthStateAsync()
    {
        CurrentUser = await _authService.GetCurrentUserAsync();
        OnAuthStateChanged?.Invoke();
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var result = await _authService.LoginAsync(request);
        if (result.Succeeded)
        {
            RequiresTwoFactor = false;
            PendingTwoFactorUserName = null;
            await RefreshAuthStateAsync();
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
        else if (result.RequiresTwoFactor)
        {
            RequiresTwoFactor = true;
            PendingTwoFactorUserName = request.UserName;
        }
        return result;
    }

    public async Task<LoginResponse> TwoFactorLoginAsync(string code, bool rememberMachine)
    {
        var result = await _authService.TwoFactorLoginAsync(code, rememberMachine);
        if (result.Succeeded)
        {
            RequiresTwoFactor = false;
            PendingTwoFactorUserName = null;
            await RefreshAuthStateAsync();
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
        return result;
    }

    public async Task<LoginResponse> RecoveryCodeLoginAsync(string code)
    {
        var result = await _authService.RecoveryCodeLoginAsync(code);
        if (result.Succeeded)
        {
            RequiresTwoFactor = false;
            PendingTwoFactorUserName = null;
            await RefreshAuthStateAsync();
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
        return result;
    }

    public void ClearTwoFactorState()
    {
        RequiresTwoFactor = false;
        PendingTwoFactorUserName = null;
    }

    public async Task LogoutAsync()
    {
        await _authService.LogoutAsync();
        CurrentUser = null;
        RequiresTwoFactor = false;
        PendingTwoFactorUserName = null;
        OnAuthStateChanged?.Invoke();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
