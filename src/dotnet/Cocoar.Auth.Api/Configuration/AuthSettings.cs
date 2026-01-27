namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Authentication and cookie configuration settings.
/// </summary>
public class AuthSettings
{
    /// <summary>
    /// Cookie configuration.
    /// </summary>
    public CookieSettings Cookie { get; init; } = new();

    /// <summary>
    /// Session expiration in days.
    /// </summary>
    public int SessionExpirationDays { get; init; } = 14;

    /// <summary>
    /// Enable sliding expiration for sessions.
    /// </summary>
    public bool SlidingExpiration { get; init; } = true;
}

/// <summary>
/// Cookie-specific settings.
/// </summary>
public class CookieSettings
{
    /// <summary>
    /// Whether cookies should be HTTP-only (prevents JavaScript access).
    /// </summary>
    public bool HttpOnly { get; init; } = true;

    /// <summary>
    /// Cookie secure policy: "Always", "SameAsRequest", or "None".
    /// In development, "None" allows HTTP; in production, use "Always".
    /// </summary>
    public string SecurePolicy { get; init; } = "SameAsRequest";

    /// <summary>
    /// SameSite cookie policy: "Strict", "Lax", or "None".
    /// </summary>
    public string SameSite { get; init; } = "Lax";
}
