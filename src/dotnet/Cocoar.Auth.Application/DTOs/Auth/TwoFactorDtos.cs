namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// Response DTO for 2FA setup containing the shared key and QR code data.
/// </summary>
public record TwoFactorSetupDto
{
    /// <summary>
    /// The shared key to be entered manually in an authenticator app.
    /// </summary>
    public required string SharedKey { get; init; }

    /// <summary>
    /// The authenticator URI for generating a QR code.
    /// Format: otpauth://totp/{issuer}:{email}?secret={key}&issuer={issuer}
    /// </summary>
    public required string AuthenticatorUri { get; init; }
}

/// <summary>
/// Request DTO for enabling 2FA with a verification code.
/// </summary>
public record EnableTwoFactorDto
{
    /// <summary>
    /// The TOTP code from the authenticator app.
    /// </summary>
    public required string Code { get; init; }
}

/// <summary>
/// Request DTO for disabling 2FA.
/// </summary>
public record DisableTwoFactorDto
{
    /// <summary>
    /// The TOTP code from the authenticator app to verify the user.
    /// </summary>
    public required string Code { get; init; }
}

/// <summary>
/// Response DTO for 2FA status.
/// </summary>
public record TwoFactorStatusDto
{
    /// <summary>
    /// Whether 2FA is enabled for the user.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Whether the user has an authenticator key set up.
    /// </summary>
    public bool HasAuthenticator { get; init; }

    /// <summary>
    /// The number of recovery codes remaining.
    /// </summary>
    public int RecoveryCodesRemaining { get; init; }
}

/// <summary>
/// Response DTO containing recovery codes.
/// </summary>
public record RecoveryCodesDto
{
    /// <summary>
    /// The list of recovery codes.
    /// </summary>
    public required List<string> Codes { get; init; }
}

/// <summary>
/// Request DTO for completing 2FA login with a TOTP code.
/// </summary>
public record TwoFactorLoginDto
{
    /// <summary>
    /// The TOTP code from the authenticator app.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Whether to remember this machine for future logins.
    /// </summary>
    public bool RememberMachine { get; init; }
}

/// <summary>
/// Request DTO for completing 2FA login with a recovery code.
/// </summary>
public record RecoveryCodeLoginDto
{
    /// <summary>
    /// The recovery code.
    /// </summary>
    public required string Code { get; init; }
}
