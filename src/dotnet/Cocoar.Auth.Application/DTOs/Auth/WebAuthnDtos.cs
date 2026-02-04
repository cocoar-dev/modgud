using System.Text.Json;

namespace Cocoar.Auth.Application.DTOs.Auth;

// ═══════════════════════════════════════════════════════════════════════════
// REGISTRATION DTOs
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response DTO for WebAuthn registration options.
/// Contains the options to be passed to navigator.credentials.create().
/// </summary>
public record WebAuthnRegistrationOptionsDto
{
    /// <summary>
    /// The JSON-serialized options for navigator.credentials.create().
    /// </summary>
    public required JsonElement Options { get; init; }
}

/// <summary>
/// Request DTO for completing WebAuthn registration.
/// Contains the credential response from navigator.credentials.create().
/// </summary>
public record CompleteWebAuthnRegistrationDto
{
    /// <summary>
    /// The attestation response JSON from the authenticator.
    /// </summary>
    public required JsonElement AttestationResponse { get; init; }

    /// <summary>
    /// Optional user-defined name for this credential/device.
    /// </summary>
    public string? DeviceName { get; init; }
}

/// <summary>
/// Response DTO for successful WebAuthn registration.
/// </summary>
public record WebAuthnRegistrationResultDto
{
    /// <summary>
    /// The credential ID (base64url encoded).
    /// </summary>
    public required string CredentialId { get; init; }

    /// <summary>
    /// The device name assigned to this credential.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// When the credential was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// AUTHENTICATION DTOs
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response DTO for WebAuthn authentication options.
/// Contains the options to be passed to navigator.credentials.get().
/// </summary>
public record WebAuthnAuthenticationOptionsDto
{
    /// <summary>
    /// The JSON-serialized options for navigator.credentials.get().
    /// </summary>
    public required JsonElement Options { get; init; }
}

/// <summary>
/// Request DTO for completing WebAuthn authentication.
/// Contains the assertion response from navigator.credentials.get().
/// </summary>
public record CompleteWebAuthnAuthenticationDto
{
    /// <summary>
    /// The assertion response JSON from the authenticator.
    /// </summary>
    public required JsonElement AssertionResponse { get; init; }

    /// <summary>
    /// Whether to remember this machine for future logins (used for 2FA flow).
    /// </summary>
    public bool RememberMachine { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// MANAGEMENT DTOs
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// DTO for displaying a WebAuthn credential to the user.
/// </summary>
public record WebAuthnCredentialDto
{
    /// <summary>
    /// The credential ID (base64url encoded).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// User-defined name for this credential/device.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// The type of authenticator (e.g., "platform", "cross-platform").
    /// </summary>
    public string? AuthenticatorType { get; init; }

    /// <summary>
    /// When the credential was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the credential was last used.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
/// DTO for listing all WebAuthn credentials.
/// </summary>
public record WebAuthnCredentialListDto
{
    /// <summary>
    /// The list of credentials.
    /// </summary>
    public required List<WebAuthnCredentialDto> Credentials { get; init; }
}

/// <summary>
/// Request DTO for renaming a WebAuthn credential.
/// </summary>
public record RenameWebAuthnCredentialDto
{
    /// <summary>
    /// The new name for the credential.
    /// </summary>
    public required string Name { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// PASSWORDLESS LOGIN DTOs
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request DTO for initiating passwordless login.
/// May include an optional username hint for conditional mediation.
/// </summary>
public record WebAuthnLoginOptionsRequestDto
{
    /// <summary>
    /// Optional username to filter credentials (for non-resident key flow).
    /// </summary>
    public string? UserName { get; init; }
}
