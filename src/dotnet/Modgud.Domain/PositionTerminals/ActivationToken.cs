namespace Modgud.Domain.PositionTerminals;

/// <summary>A physical WebAuthn authenticator owned by a position/team rather
/// than by a person. One logical token can carry one credential per RP ID and
/// can be assigned to multiple positions.</summary>
public sealed class ActivationToken
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public ActivationTokenStatus Status { get; set; } = ActivationTokenStatus.PendingRegistration;
    public List<Guid> AssignedPositionIds { get; set; } = [];
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum ActivationTokenStatus
{
    PendingRegistration,
    Active,
    Disabled,
    Revoked,
}

/// <summary>RP-bound credential of an <see cref="ActivationToken"/>. Kept
/// separate from person passkeys because their ownership and lifecycle are
/// intentionally different.</summary>
public sealed class ActivationTokenCredential
{
    public Guid Id { get; set; }
    public Guid ActivationTokenId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public byte[] UserHandle { get; set; } = [];
    public uint SignatureCount { get; set; }
    public Guid AaGuid { get; set; }
    public string RpId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Single-use attestation ceremony issued to an enrolled terminal,
/// which guarantees that registration runs under the terminal application's
/// RP-compatible origin.</summary>
public sealed class ActivationTokenRegistrationCeremony
{
    public Guid Id { get; set; }
    public Guid ActivationTokenId { get; set; }
    public Guid TerminalEnrollmentId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string RpId { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
