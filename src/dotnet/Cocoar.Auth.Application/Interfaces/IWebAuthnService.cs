using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for managing WebAuthn/FIDO2 authentication.
/// </summary>
public interface IWebAuthnService
{
    // ═══════════════════════════════════════════════════════════════════════
    // REGISTRATION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates registration options for a new WebAuthn credential.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Registration options to pass to navigator.credentials.create().</returns>
    Task<ErrorOr<WebAuthnRegistrationOptionsDto>> GetRegistrationOptionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the registration of a WebAuthn credential.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="attestationResponse">The attestation response from the authenticator.</param>
    /// <param name="deviceName">Optional user-defined name for the credential.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The registration result.</returns>
    Task<ErrorOr<WebAuthnRegistrationResultDto>> CompleteRegistrationAsync(
        Guid userId,
        JsonElement attestationResponse,
        string? deviceName,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════════
    // AUTHENTICATION (2FA)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates authentication options for WebAuthn login/2FA.
    /// </summary>
    /// <param name="userId">The user's ID (optional for passwordless).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication options to pass to navigator.credentials.get().</returns>
    Task<ErrorOr<WebAuthnAuthenticationOptionsDto>> GetAuthenticationOptionsAsync(
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a WebAuthn authentication assertion.
    /// </summary>
    /// <param name="userId">The user's ID (for 2FA flow).</param>
    /// <param name="assertionResponse">The assertion response from the authenticator.</param>
    /// <param name="ipAddress">The IP address for audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success and user ID (for passwordless) or error.</returns>
    Task<ErrorOr<Guid>> VerifyAuthenticationAsync(
        Guid? userId,
        JsonElement assertionResponse,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════════
    // CREDENTIAL MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets all WebAuthn credentials for a user.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of credentials.</returns>
    Task<ErrorOr<WebAuthnCredentialListDto>> GetCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a WebAuthn credential.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="credentialId">The credential ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<ErrorOr<bool>> DeleteCredentialAsync(
        Guid userId,
        string credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a WebAuthn credential.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="credentialId">The credential ID to rename.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<ErrorOr<bool>> RenameCredentialAsync(
        Guid userId,
        string credentialId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of registered WebAuthn credentials for a user.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential count.</returns>
    Task<int> GetCredentialCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
