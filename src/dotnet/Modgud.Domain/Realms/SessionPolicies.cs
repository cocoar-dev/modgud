namespace Modgud.Domain.Realms;

/// <summary>
/// Realm-owned policy for the shared Modgud browser/SSO cookie. The cookie and
/// its authoritative <c>UserSession</c> row consume the same values.
/// </summary>
public record BrowserSessionPolicy
{
    public static BrowserSessionPolicy Defaults { get; } = new();

    /// <summary>Sliding inactivity window. Default preserves the former 30-day cookie window.</summary>
    public TimeSpan IdleLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Hard limit measured from the interactive sign-in; activity never extends it.</summary>
    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromDays(180);

    /// <summary>Whether callers may request a browser-persistent cookie.</summary>
    public bool AllowRememberMe { get; init; } = true;
}

/// <summary>
/// Policy for a native OAuth client/device session. Access-token lifetime is
/// intentionally separate; this policy controls how long a rotating refresh
/// chain may continue without a full user sign-in.
/// </summary>
public record ClientSessionPolicy
{
    public static ClientSessionPolicy Defaults { get; } = new();

    public TimeSpan IdleLifetime { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromDays(365);
}
