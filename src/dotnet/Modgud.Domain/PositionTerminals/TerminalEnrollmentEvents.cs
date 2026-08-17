namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// Stream events for <see cref="TerminalEnrollment"/> (MG-FT-03). The document
/// is the inline projection; actor + time of every transition live here.
/// <c>Enrolled</c> is emitted by the device-flow enrollment (MG-FT-04) and is
/// the only event that may ever set the DPoP key — a second key means a fresh
/// slot, never a silent rotation.
/// </summary>
public record TerminalEnrollmentCreated(
    Guid Id,
    Guid PositionPrincipalId,
    string DisplayName,
    string? Location,
    Guid OAuthApplicationId,
    string ClientId,
    string WebAuthnRpId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    string? Binding = null,
    IReadOnlyList<Guid>? AllowedPositionIds = null);

public record TerminalAllowedPositionsChanged(
    Guid Id,
    IReadOnlyList<Guid> AllowedPositionIds,
    Guid ChangedByUserId,
    DateTimeOffset ChangedAt);

public record TerminalEnrollmentDetailsChanged(
    Guid Id,
    string DisplayName,
    string? Location);

public record TerminalEnrollmentEnrolled(
    Guid Id,
    string? DpopJkt,
    string EnrollmentAuthorizationId,
    DateTimeOffset EnrolledAt);

public record TerminalEnrollmentDisabled(
    Guid Id,
    Guid DisabledByUserId,
    DateTimeOffset DisabledAt);

public record TerminalEnrollmentReactivated(
    Guid Id,
    Guid ReactivatedByUserId,
    DateTimeOffset ReactivatedAt);

public record TerminalEnrollmentRevoked(
    Guid Id,
    Guid RevokedByUserId,
    DateTimeOffset RevokedAt);

/// <summary>The terminal's activation lock (MG-FT-05, plan §13.5): appended
/// with a stream-version guard (FetchForWriting) so two racing taps can never
/// both win — the loser's append conflicts and retries the flow.</summary>
public record TerminalStaffingSessionActivated(
    Guid Id,
    Guid StaffingSessionId,
    DateTimeOffset ActivatedAt);

/// <summary>Clears the pointer when a session ends — only if it still points
/// at that session (a newer activation may already own the slot).</summary>
public record TerminalStaffingSessionCleared(
    Guid Id,
    Guid StaffingSessionId,
    DateTimeOffset ClearedAt);
