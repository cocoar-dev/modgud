namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Configuration settings for WebAuthn/FIDO2 authentication.
/// </summary>
public class WebAuthnSettings : IWebAuthnSettings
{
    /// <summary>
    /// The relying party ID (usually the domain name).
    /// Example: "localhost" or "cocoar.local"
    /// </summary>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>
    /// The relying party name displayed to users.
    /// Example: "Cocoar Auth"
    /// </summary>
    public string RelyingPartyName { get; set; } = "Cocoar Auth";

    /// <summary>
    /// Allowed origins for WebAuthn operations.
    /// Example: ["http://localhost", "http://localhost:4200"]
    /// </summary>
    public string[] Origins { get; set; } = [];

    /// <summary>
    /// Timeout in milliseconds for WebAuthn operations.
    /// Default: 60000 (60 seconds)
    /// </summary>
    public uint Timeout { get; set; } = 60000;
}

/// <summary>
/// Interface for WebAuthn settings for DI.
/// </summary>
public interface IWebAuthnSettings
{
    string RelyingPartyId { get; }
    string RelyingPartyName { get; }
    string[] Origins { get; }
    uint Timeout { get; }
}
