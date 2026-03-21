namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Server configuration settings (URL, SSL certificate).
/// </summary>
public class ServerSettings
{
    /// <summary>
    /// The URL the server listens on. Default: http://0.0.0.0:80
    /// Set to https://0.0.0.0:443 for HTTPS with a certificate.
    /// </summary>
    public string AppUrl { get; set; } = "http://0.0.0.0:80";

    /// <summary>
    /// Path to a PFX certificate file for HTTPS/TLS.
    /// When set, Kestrel is configured to use this certificate.
    /// </summary>
    public string? CertPath { get; set; }

    /// <summary>
    /// Password for the PFX certificate file.
    /// </summary>
    public string? CertPassword { get; set; }
}
