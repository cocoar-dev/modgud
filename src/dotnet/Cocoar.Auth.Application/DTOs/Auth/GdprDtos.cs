namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// Request DTO for initiating account deletion.
/// </summary>
public record RequestDeletionDto
{
    /// <summary>
    /// The user's current password for verification.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// Optional reason for deletion.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Request DTO for confirming account deletion.
/// </summary>
public record ConfirmDeletionDto
{
    /// <summary>
    /// The confirmation token sent via email.
    /// </summary>
    public required string Token { get; init; }
}

/// <summary>
/// Response DTO for deletion request.
/// </summary>
public record DeletionRequestDto
{
    /// <summary>
    /// When the deletion was requested.
    /// </summary>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Deadline to confirm the deletion.
    /// </summary>
    public DateTimeOffset ConfirmationDeadline { get; init; }

    /// <summary>
    /// Message describing next steps.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// DTO for deletion status.
/// </summary>
public record DeletionStatusDto
{
    /// <summary>
    /// Whether a deletion request is pending.
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>
    /// Whether the user is soft-deleted.
    /// </summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// Whether the user's data has been masked (GDPR erased).
    /// </summary>
    public bool IsDataMasked { get; init; }

    /// <summary>
    /// When the deletion was requested (if pending).
    /// </summary>
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>
    /// Deadline to confirm (if pending).
    /// </summary>
    public DateTimeOffset? ConfirmationDeadline { get; init; }
}

/// <summary>
/// DTO for GDPR data export (Article 20 - Right to Data Portability).
/// </summary>
public record UserDataExportDto
{
    /// <summary>
    /// Export metadata.
    /// </summary>
    public required ExportMetadataDto Metadata { get; init; }

    /// <summary>
    /// User profile information.
    /// </summary>
    public required ExportProfileDto Profile { get; init; }

    /// <summary>
    /// Security-related information (non-sensitive).
    /// </summary>
    public required ExportSecurityDto Security { get; init; }

    /// <summary>
    /// User's roles.
    /// </summary>
    public required List<string> Roles { get; init; }

    /// <summary>
    /// User's claims.
    /// </summary>
    public required List<ExportClaimDto> Claims { get; init; }

    /// <summary>
    /// Active sessions.
    /// </summary>
    public required List<ExportSessionDto> Sessions { get; init; }

    /// <summary>
    /// Login history (recent).
    /// </summary>
    public required List<ExportLoginEventDto> LoginHistory { get; init; }
}

/// <summary>
/// Export metadata.
/// </summary>
public record ExportMetadataDto
{
    /// <summary>
    /// When the export was generated.
    /// </summary>
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>
    /// Export format version.
    /// </summary>
    public required string FormatVersion { get; init; }

    /// <summary>
    /// The user ID.
    /// </summary>
    public Guid UserId { get; init; }
}

/// <summary>
/// Exported profile data.
/// </summary>
public record ExportProfileDto
{
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public bool EmailConfirmed { get; init; }
    public string? PhoneNumber { get; init; }
    public bool PhoneNumberConfirmed { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Exported security information (non-sensitive).
/// </summary>
public record ExportSecurityDto
{
    public bool TwoFactorEnabled { get; init; }
    public bool LockoutEnabled { get; init; }
    public DateTimeOffset? LockoutEnd { get; init; }
    public int AccessFailedCount { get; init; }
}

/// <summary>
/// Exported claim.
/// </summary>
public record ExportClaimDto
{
    public required string Type { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Exported session.
/// </summary>
public record ExportSessionDto
{
    public string? IpAddress { get; init; }
    public string? Browser { get; init; }
    public string? OperatingSystem { get; init; }
    public string? DeviceType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; init; }
}

/// <summary>
/// Exported login event.
/// </summary>
public record ExportLoginEventDto
{
    public DateTimeOffset Timestamp { get; init; }
    public bool Success { get; init; }
    public string? IpAddress { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Request DTO for admin soft delete.
/// </summary>
public record AdminSoftDeleteDto
{
    /// <summary>
    /// Optional reason for deletion.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Request DTO for admin restore.
/// </summary>
public record AdminRestoreDto
{
    /// <summary>
    /// Optional reason for restoration.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Request DTO for permanent data erasure (GDPR).
/// </summary>
public record AdminPermanentEraseDto
{
    /// <summary>
    /// Required reason for permanent erasure (for audit).
    /// </summary>
    public required string Reason { get; init; }
}
