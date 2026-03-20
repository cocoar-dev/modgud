using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for managing external login provider authentication flows.
/// </summary>
public class ExternalLoginService : IExternalLoginService
{
    private readonly ILoginProviderRepository _loginProviderRepository;
    private readonly IOidcProtocolService _oidcProtocolService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDocumentSession _session;
    private readonly ILoginAuditService _loginAuditService;
    private readonly IAuthenticationService _authenticationService;

    public ExternalLoginService(
        ILoginProviderRepository loginProviderRepository,
        IOidcProtocolService oidcProtocolService,
        UserManager<ApplicationUser> userManager,
        IDocumentSession session,
        ILoginAuditService loginAuditService,
        IAuthenticationService authenticationService)
    {
        _loginProviderRepository = loginProviderRepository;
        _oidcProtocolService = oidcProtocolService;
        _userManager = userManager;
        _session = session;
        _loginAuditService = loginAuditService;
        _authenticationService = authenticationService;
    }

    public async Task<ExternalProviderListDto> GetAvailableProvidersAsync(CancellationToken cancellationToken = default)
    {
        var allProviders = await _loginProviderRepository.GetAllAsync(cancellationToken);

        var externalProviders = allProviders.Items
            .Where(p => p.Type == LoginProviderType.OpenIdConnect)
            .Select(p => new ExternalProviderDto
            {
                Name = p.Name,
                DisplayName = p.DisplayName,
                Type = p.Type.ToString()
            })
            .ToList();

        return new ExternalProviderListDto { Providers = externalProviders };
    }

    public async Task<ErrorOr<ExternalLoginRedirectDto>> InitiateLoginAsync(
        string providerName,
        string callbackUrl,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        return await InitiateFlowAsync(providerName, callbackUrl, returnUrl, null, cancellationToken);
    }

    public async Task<ErrorOr<ExternalLoginRedirectDto>> InitiateLinkAsync(
        Guid userId,
        string providerName,
        string callbackUrl,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        return await InitiateFlowAsync(providerName, callbackUrl, returnUrl, userId, cancellationToken);
    }

    public async Task<ErrorOr<ExternalLoginCallbackResult>> ProcessCallbackAsync(
        string code,
        string state,
        string callbackUrl,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        // 1. Load and delete the state (one-time use)
        var loginState = await _session.Query<ExternalLoginState>()
            .FirstOrDefaultAsync(s => s.State == state, cancellationToken);

        if (loginState is null || loginState.IsExpired)
        {
            return ExternalLoginErrors.InvalidState;
        }

        _session.Delete(loginState);

        // 2. Load provider config
        var provider = await _loginProviderRepository.GetByNameAsync(loginState.ProviderName, cancellationToken);
        if (provider is null)
        {
            return ExternalLoginErrors.ProviderNotFound(loginState.ProviderName);
        }

        var oidcConfig = GetOidcConfig(provider);
        if (oidcConfig is null)
        {
            return ExternalLoginErrors.MissingConfiguration("Authority, ClientId, or ClientSecret");
        }

        // 3. Exchange code for tokens
        var tokenResponse = await _oidcProtocolService.ExchangeCodeAsync(
            oidcConfig, code, callbackUrl, loginState.CodeVerifier, cancellationToken);

        if (tokenResponse is null)
        {
            return ExternalLoginErrors.TokenExchangeFailed;
        }

        // 4. Validate ID token
        var userInfo = await _oidcProtocolService.ValidateIdTokenAsync(
            oidcConfig, tokenResponse.IdToken, loginState.Nonce, cancellationToken);

        if (userInfo is null)
        {
            return ExternalLoginErrors.IdTokenValidationFailed;
        }

        // 5. Handle account linking
        if (loginState.LinkToUserId.HasValue)
        {
            return await HandleAccountLinkingAsync(
                loginState.LinkToUserId.Value, provider.Name, provider.DisplayName,
                userInfo.Subject, loginState.ReturnUrl, cancellationToken);
        }

        // 6. Find existing user by login
        var existingUser = await _userManager.FindByLoginAsync(provider.Name, userInfo.Subject);

        if (existingUser is not null)
        {
            return await HandleExistingUserLoginAsync(
                existingUser, ipAddress, userAgent, loginState.ReturnUrl, cancellationToken);
        }

        // 7. Auto-create user
        return await HandleAutoCreateUserAsync(
            provider.Name, provider.DisplayName, userInfo,
            ipAddress, userAgent, loginState.ReturnUrl, cancellationToken);
    }

    public async Task<ErrorOr<bool>> UnlinkAsync(
        Guid userId,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        // Check if this is the only login method
        var hasPassword = await _userManager.HasPasswordAsync(user);
        var logins = await _userManager.GetLoginsAsync(user);
        var matchingLogin = logins.FirstOrDefault(l => l.LoginProvider == providerName);

        if (matchingLogin is null)
        {
            return ExternalLoginErrors.ProviderNotFound(providerName);
        }

        if (!hasPassword && logins.Count <= 1)
        {
            return ExternalLoginErrors.CannotUnlinkOnlyLogin;
        }

        var result = await _userManager.RemoveLoginAsync(user, matchingLogin.LoginProvider, matchingLogin.ProviderKey);
        if (!result.Succeeded)
        {
            return Error.Failure(description: string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        return true;
    }

    public async Task<LinkedExternalLoginListDto> GetLinkedLoginsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return new LinkedExternalLoginListDto { Logins = [] };
        }

        var logins = await _userManager.GetLoginsAsync(user);
        var linked = logins.Select(l => new LinkedExternalLoginDto
        {
            ProviderName = l.LoginProvider,
            ProviderDisplayName = l.ProviderDisplayName
        }).ToList();

        return new LinkedExternalLoginListDto { Logins = linked };
    }

    private async Task<ErrorOr<ExternalLoginRedirectDto>> InitiateFlowAsync(
        string providerName,
        string callbackUrl,
        string returnUrl,
        Guid? linkToUserId,
        CancellationToken cancellationToken)
    {
        var provider = await _loginProviderRepository.GetByNameAsync(providerName, cancellationToken);
        if (provider is null)
        {
            return ExternalLoginErrors.ProviderNotFound(providerName);
        }

        if (provider.Type != LoginProviderType.OpenIdConnect)
        {
            return ExternalLoginErrors.ProviderNotOidc(providerName);
        }

        var oidcConfig = GetOidcConfig(provider);
        if (oidcConfig is null)
        {
            return ExternalLoginErrors.MissingConfiguration("Authority, ClientId, or ClientSecret");
        }

        // Generate PKCE and state
        var codeVerifier = PkceHelper.GenerateCodeVerifier();
        var codeChallenge = PkceHelper.ComputeCodeChallenge(codeVerifier);
        var state = PkceHelper.GenerateState();
        var nonce = PkceHelper.GenerateNonce();

        // Store state for callback validation
        var loginState = ExternalLoginState.Create(
            state, nonce, codeVerifier, providerName, returnUrl, linkToUserId);

        _session.Store(loginState);
        await _session.SaveChangesAsync(cancellationToken);

        // Build authorization URL
        var authUrl = await _oidcProtocolService.BuildAuthorizationUrlAsync(
            oidcConfig, callbackUrl, state, nonce, codeChallenge, cancellationToken);

        return new ExternalLoginRedirectDto { RedirectUrl = authUrl };
    }

    private async Task<ErrorOr<ExternalLoginCallbackResult>> HandleAccountLinkingAsync(
        Guid userId,
        string providerName,
        string? providerDisplayName,
        string subject,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        // Check if this external login is already linked to someone
        var existingUser = await _userManager.FindByLoginAsync(providerName, subject);
        if (existingUser is not null)
        {
            return ExternalLoginErrors.ExternalLoginAlreadyLinked;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        var loginInfo = new UserLoginInfo(providerName, subject, providerDisplayName);
        var result = await _userManager.AddLoginAsync(user, loginInfo);

        if (!result.Succeeded)
        {
            return Error.Failure(description: string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await _session.SaveChangesAsync(cancellationToken);

        return new ExternalLoginCallbackResult
        {
            ReturnUrl = returnUrl,
            UserId = userId,
            IsLinkOperation = true
        };
    }

    private async Task<ErrorOr<ExternalLoginCallbackResult>> HandleExistingUserLoginAsync(
        ApplicationUser user,
        string? ipAddress,
        string? userAgent,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        if (!user.IsActive)
        {
            return ExternalLoginErrors.UserAccountInactive;
        }

        // Check if 2FA is required
        if (user.TwoFactorEnabled)
        {
            await _authenticationService.StoreTwoFactorUserAsync(user, cancellationToken);

            return new ExternalLoginCallbackResult
            {
                ReturnUrl = returnUrl,
                UserId = user.Id,
                RequiresTwoFactor = true
            };
        }

        // Sign in directly
        await _authenticationService.SignInAsync(user, isPersistent: false, cancellationToken);
        await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);

        return new ExternalLoginCallbackResult
        {
            ReturnUrl = returnUrl,
            UserId = user.Id
        };
    }

    private async Task<ErrorOr<ExternalLoginCallbackResult>> HandleAutoCreateUserAsync(
        string providerName,
        string? providerDisplayName,
        OidcUserInfo userInfo,
        string? ipAddress,
        string? userAgent,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        // Determine username: prefer email, fallback to provider_subject
        var userName = userInfo.Email ?? $"{providerName}_{userInfo.Subject}";

        // Check if username already exists
        var existingByName = await _userManager.FindByNameAsync(userName);
        if (existingByName is not null)
        {
            // Append a random suffix
            userName = $"{providerName}_{userInfo.Subject}";
            existingByName = await _userManager.FindByNameAsync(userName);
            if (existingByName is not null)
            {
                userName = $"{providerName}_{Guid.NewGuid():N}";
            }
        }

        var user = new ApplicationUser(userName, userInfo.Email);
        user.SetFirstName(userInfo.GivenName);
        user.SetLastName(userInfo.FamilyName);

        if (userInfo is { Email: not null, EmailVerified: true })
        {
            user.SetEmailConfirmed(true);
        }

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            return Error.Failure(description: string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        // Link the external login
        var loginInfo = new UserLoginInfo(providerName, userInfo.Subject, providerDisplayName);
        var addLoginResult = await _userManager.AddLoginAsync(user, loginInfo);
        if (!addLoginResult.Succeeded)
        {
            return Error.Failure(description: string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));
        }

        // Sign in
        await _authenticationService.SignInAsync(user, isPersistent: false, cancellationToken);
        await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);

        return new ExternalLoginCallbackResult
        {
            ReturnUrl = returnUrl,
            UserId = user.Id
        };
    }

    private static OidcProviderConfig? GetOidcConfig(Application.DTOs.LoginProviders.LoginProviderDto provider)
    {
        if (!provider.Configuration.TryGetValue("Authority", out var authority) ||
            !provider.Configuration.TryGetValue("ClientId", out var clientId) ||
            !provider.Configuration.TryGetValue("ClientSecret", out var clientSecret))
        {
            return null;
        }

        provider.Configuration.TryGetValue("Scopes", out var scopes);

        return new OidcProviderConfig(authority, clientId, clientSecret, scopes);
    }
}
