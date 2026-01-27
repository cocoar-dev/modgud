namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// CORS (Cross-Origin Resource Sharing) configuration settings.
/// </summary>
public class CorsSettings
{
    /// <summary>
    /// Allowed origins for CORS requests.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Whether to allow credentials (cookies, authorization headers).
    /// </summary>
    public bool AllowCredentials { get; init; } = true;

    /// <summary>
    /// Allowed HTTP methods. Empty array means allow any method.
    /// </summary>
    public string[] AllowedMethods { get; init; } = [];

    /// <summary>
    /// Allowed HTTP headers. Empty array means allow any header.
    /// </summary>
    public string[] AllowedHeaders { get; init; } = [];
}
