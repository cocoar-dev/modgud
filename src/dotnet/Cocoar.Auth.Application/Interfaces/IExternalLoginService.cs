using Cocoar.Auth.Application.DTOs.Auth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for managing external login provider authentication flows.
/// </summary>
public interface IExternalLoginService
{
    /// <summary>
    /// Gets available external login providers (OIDC providers only, no secrets).
    /// </summary>
    Task<ExternalProviderListDto> GetAvailableProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates an external login flow by building the authorization URL.
    /// </summary>
    Task<ErrorOr<ExternalLoginRedirectDto>> InitiateLoginAsync(
        string providerName,
        string callbackUrl,
        string returnUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates an account-linking flow for an authenticated user.
    /// </summary>
    Task<ErrorOr<ExternalLoginRedirectDto>> InitiateLinkAsync(
        Guid userId,
        string providerName,
        string callbackUrl,
        string returnUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes the OIDC callback after the user returns from the provider.
    /// </summary>
    Task<ErrorOr<ExternalLoginCallbackResult>> ProcessCallbackAsync(
        string code,
        string state,
        string callbackUrl,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlinks an external login from a user.
    /// </summary>
    Task<ErrorOr<bool>> UnlinkAsync(
        Guid userId,
        string providerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the external logins linked to a user.
    /// </summary>
    Task<LinkedExternalLoginListDto> GetLinkedLoginsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from processing an external login callback.
/// </summary>
public record ExternalLoginCallbackResult
{
    /// <summary>
    /// The return URL to redirect the user to.
    /// </summary>
    public required string ReturnUrl { get; init; }

    /// <summary>
    /// The user ID that was authenticated.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Whether the user needs to complete 2FA.
    /// </summary>
    public bool RequiresTwoFactor { get; init; }

    /// <summary>
    /// Whether this was an account-linking operation.
    /// </summary>
    public bool IsLinkOperation { get; init; }
}
