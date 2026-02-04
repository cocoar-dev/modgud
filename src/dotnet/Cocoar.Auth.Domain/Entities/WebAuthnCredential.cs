using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a WebAuthn credential registered for a user.
/// </summary>
public class WebAuthnCredential
{
    /// <summary>
    /// The unique credential ID (base64url encoded).
    /// </summary>
    [JsonInclude]
    public required string CredentialId { get; set; }

    /// <summary>
    /// The public key (COSE format, base64 encoded).
    /// </summary>
    [JsonInclude]
    public required byte[] PublicKey { get; set; }

    /// <summary>
    /// The user handle (user ID in bytes, base64 encoded).
    /// </summary>
    [JsonInclude]
    public required byte[] UserHandle { get; set; }

    /// <summary>
    /// The signature counter for replay protection.
    /// </summary>
    [JsonInclude]
    public uint SignCount { get; set; }

    /// <summary>
    /// User-defined name for the credential/device.
    /// </summary>
    [JsonInclude]
    public string? DeviceName { get; set; }

    /// <summary>
    /// The type of authenticator (platform, cross-platform).
    /// </summary>
    [JsonInclude]
    public string? AuthenticatorType { get; set; }

    /// <summary>
    /// The AAGUID of the authenticator (if available).
    /// </summary>
    [JsonInclude]
    public Guid? Aaguid { get; set; }

    /// <summary>
    /// When the credential was registered.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the credential was last used for authentication.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// The credential type (e.g., "public-key").
    /// </summary>
    [JsonInclude]
    public string CredentialType { get; set; } = "public-key";

    /// <summary>
    /// The transports supported by this credential.
    /// </summary>
    [JsonInclude]
    public string[]? Transports { get; set; }
}
