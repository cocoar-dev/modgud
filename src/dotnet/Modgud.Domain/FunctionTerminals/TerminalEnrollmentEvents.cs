namespace Modgud.Domain.FunctionTerminals;

/// <summary>
/// Stream events for <see cref="TerminalEnrollment"/> (MG-FT-03). The document
/// is the inline projection; actor + time of every transition live here.
/// <c>Enrolled</c> is emitted by the device-flow enrollment (MG-FT-04) and is
/// the only event that may ever set the DPoP key — a second key means a fresh
/// slot, never a silent rotation.
/// </summary>
public record TerminalEnrollmentCreated(
    Guid Id,
    Guid FunctionPrincipalId,
    string DisplayName,
    string? Location,
    Guid OAuthApplicationId,
    string ClientId,
    string WebAuthnRpId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt);

public record TerminalEnrollmentDetailsChanged(
    Guid Id,
    string DisplayName,
    string? Location);

public record TerminalEnrollmentEnrolled(
    Guid Id,
    string DpopJkt,
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
