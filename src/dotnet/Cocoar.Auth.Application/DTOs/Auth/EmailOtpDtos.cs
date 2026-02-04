namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// Request DTO for requesting an email OTP code.
/// No body required - triggers sending OTP to authenticated user's email.
/// </summary>
public record RequestEmailOtpDto;

/// <summary>
/// Request DTO for verifying an email OTP code.
/// </summary>
public record VerifyEmailOtpDto
{
    /// <summary>
    /// The 6-digit OTP code sent to the user's email.
    /// </summary>
    public required string Code { get; init; }
}

/// <summary>
/// Request DTO for completing login with an email OTP code.
/// </summary>
public record EmailOtpLoginDto
{
    /// <summary>
    /// The 6-digit OTP code sent to the user's email.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Whether to remember this machine for future logins.
    /// </summary>
    public bool RememberMachine { get; init; }
}

/// <summary>
/// Response DTO for email OTP status.
/// </summary>
public record EmailOtpStatusDto
{
    /// <summary>
    /// Whether an OTP challenge is currently pending.
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>
    /// Seconds until the current OTP expires (null if no pending OTP).
    /// </summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>
    /// Number of attempts remaining (null if no pending OTP).
    /// </summary>
    public int? AttemptsRemaining { get; init; }

    /// <summary>
    /// Whether the user can request a new OTP (based on rate limiting).
    /// </summary>
    public bool CanRequestNew { get; init; }
}
