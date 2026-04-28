namespace TimeToDo.Authentication.Domain;

/// <summary>
/// Stores a WebAuthn/Passkey credential for a user.
/// Each user can have multiple passkeys (e.g. fingerprint + hardware key).
/// Persisted as a Marten document (not in event stream — contains raw crypto data).
/// </summary>
public class StoredPasskeyCredential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The credential ID assigned by the authenticator.</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>The public key in COSE format.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>User handle (used by the authenticator to identify the user).</summary>
    public byte[] UserHandle { get; set; } = [];

    /// <summary>Signature counter — incremented by the authenticator on each use. Used to detect cloned keys.</summary>
    public uint SignatureCount { get; set; }

    /// <summary>The attestation type (e.g. "none", "packed").</summary>
    public string AttestationType { get; set; } = string.Empty;

    /// <summary>The authenticator's AAGUID.</summary>
    public Guid AaGuid { get; set; }

    /// <summary>Human-readable name for this passkey (e.g. "Windows Hello", "YubiKey").</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}
