namespace Modgud.Authentication.Gdpr;

/// <summary>
/// Request body for the user-initiated deletion endpoint. The current
/// password is required to prevent CSRF-style account takeover from a
/// stolen session cookie.
/// </summary>
public record RequestDeletionDto
{
    public required string Password { get; init; }
    public string? Reason { get; init; }
}

public record DeletionRequestResponseDto
{
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>The grace deadline — when the account is auto-erased unless the
    /// user cancels. (Name kept for wire compatibility with existing clients.)</summary>
    public DateTimeOffset ConfirmationDeadline { get; init; }
    public required string Message { get; init; }
}

public record DeletionStatusDto
{
    public bool IsPending { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsDataMasked { get; init; }

    /// <summary>Who initiated the pending deletion — drives the SPA: a
    /// SelfService pending user sees the cancel interstitial, an Admin
    /// recycle-bin user cannot self-cancel. Null when not pending.</summary>
    public DeletionInitiator? Initiator { get; init; }
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>The grace / retention deadline. (Name kept for wire compatibility.)</summary>
    public DateTimeOffset? ConfirmationDeadline { get; init; }
}

/// <summary>Article 20 — right to data portability. JSON dump of everything
/// the IdP holds about the caller.</summary>
public record UserDataExportDto
{
    public required ExportMetadataDto Metadata { get; init; }
    public required ExportProfileDto Profile { get; init; }
    public required ExportSecurityDto Security { get; init; }
    public required List<string> Permissions { get; init; }
    public required List<ExportSessionDto> Sessions { get; init; }
    public required List<ExportLoginEventDto> LoginHistory { get; init; }
}

public record ExportMetadataDto
{
    public DateTimeOffset ExportedAt { get; init; }
    public required string FormatVersion { get; init; }
    public Guid UserId { get; init; }
}

public record ExportProfileDto
{
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public bool EmailConfirmed { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? Acronym { get; init; }
    public bool IsActive { get; init; }
}

public record ExportSecurityDto
{
    public bool TwoFactorEnabled { get; init; }
    public bool EmailOtpEnabled { get; init; }
    public bool LockoutEnabled { get; init; }
    public DateTimeOffset? LockoutEnd { get; init; }
    public int AccessFailedCount { get; init; }
}

public record ExportSessionDto
{
    public required string Kind { get; init; }
    public string? ClientId { get; init; }
    public string? ClientDisplayName { get; init; }
    public string? IpAddress { get; init; }
    public string? Browser { get; init; }
    public string? OperatingSystem { get; init; }
    public string? DeviceType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset AbsoluteExpiresAt { get; init; }
}

public record ExportLoginEventDto
{
    public DateTimeOffset Timestamp { get; init; }
    public bool Success { get; init; }
    public string? IpAddress { get; init; }
}

/// <summary>Body for the admin permanent-erase endpoint — the reason is
/// captured in the audit log and Marten masking headers.</summary>
public record AdminPermanentEraseDto
{
    public required string Reason { get; init; }
}
