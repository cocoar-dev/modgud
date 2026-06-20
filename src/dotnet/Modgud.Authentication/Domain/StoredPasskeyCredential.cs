namespace Modgud.Authentication.Domain;

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

    /// <summary>
    /// The WebAuthn RP ID this credential was enrolled under (ADR-0009 per-client
    /// RP-ID). <c>null</c> = legacy realm-scoped, i.e. the effective RP ID is the
    /// realm's <c>PrimaryDomain</c> (the only behaviour before per-client RP-IDs).
    /// Stored as the resolved host string, NOT a client id — two OAuth clients may
    /// legitimately share one RP ID, and a passkey is cryptographically bound to the
    /// RP ID, so credential lookup filters on RP ID, not client. Existing documents
    /// deserialize with <c>null</c> (no migration).
    /// </summary>
    public string? RpId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}
