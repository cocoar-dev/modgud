namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// Short-lived server-side ticket for the TERMINAL variant of the hosted
/// device-verification flow (MG-FT-04, plan §11.3): when the user_code being
/// verified belongs to a terminal-managed client, the SPA gets this ticket
/// instead of the person-flow <c>DeviceVerificationTicket</c> — the two flows
/// stay semantically separate. Ephemeral like the passkey ceremonies: a plain
/// document, not event-sourced; single-use via <see cref="ConsumedAt"/>.
/// </summary>
public sealed class TerminalEnrollmentVerificationTicket
{
    public Guid Id { get; set; }
    public Guid ApprovingAdminUserId { get; set; }
    public Guid TerminalEnrollmentId { get; set; }
    public string UserCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;
}
