using Marten.Events.Aggregation;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

/// <summary>
/// Builds <see cref="TerminalEnrollment"/> documents inline from enrollment
/// streams (MG-FT-03).
/// </summary>
public partial class TerminalEnrollmentProjection : SingleStreamProjection<TerminalEnrollment, Guid>
{
    public TerminalEnrollment Create(TerminalEnrollmentCreated e) => new()
    {
        Id = e.Id,
        PositionPrincipalId = e.PositionPrincipalId,
        AllowedPositionIds = e.AllowedPositionIds is { Count: > 0 }
            ? [.. e.AllowedPositionIds]
            : [e.PositionPrincipalId],
        DisplayName = e.DisplayName,
        Location = e.Location,
        OAuthApplicationId = e.OAuthApplicationId,
        ClientId = e.ClientId,
        WebAuthnRpId = e.WebAuthnRpId,
        Binding = string.IsNullOrWhiteSpace(e.Binding) ? "dpop" : e.Binding,
        Status = TerminalEnrollmentStatus.Pending,
        CreatedAt = e.CreatedAt,
        CreatedByUserId = e.CreatedByUserId,
    };

    public void Apply(TerminalEnrollmentDetailsChanged e, TerminalEnrollment terminal)
    {
        terminal.DisplayName = e.DisplayName;
        terminal.Location = e.Location;
    }

    public void Apply(TerminalAllowedPositionsChanged e, TerminalEnrollment terminal)
        => terminal.AllowedPositionIds = [.. e.AllowedPositionIds];

    public void Apply(TerminalEnrollmentEnrolled e, TerminalEnrollment terminal)
    {
        // The endpoint layer guarantees Enrolled is only ever appended once per
        // stream (DpopJkt is immutable; rotation = a fresh slot).
        terminal.DpopJkt = e.DpopJkt;
        terminal.EnrollmentAuthorizationId = e.EnrollmentAuthorizationId;
        terminal.EnrolledAt = e.EnrolledAt;
        terminal.Status = TerminalEnrollmentStatus.Active;
    }

    public void Apply(TerminalEnrollmentDisabled e, TerminalEnrollment terminal)
    {
        terminal.Status = TerminalEnrollmentStatus.Disabled;
        terminal.DisabledAt = e.DisabledAt;
    }

    public void Apply(TerminalEnrollmentReactivated e, TerminalEnrollment terminal)
    {
        // Back to where the slot stood before the disable: Active once a key is
        // enrolled, otherwise still Pending (waiting for MG-FT-04's flow).
        terminal.Status = terminal.EnrollmentAuthorizationId is null
            ? TerminalEnrollmentStatus.Pending
            : TerminalEnrollmentStatus.Active;
        terminal.DisabledAt = null;
    }

    public void Apply(TerminalEnrollmentRevoked e, TerminalEnrollment terminal)
    {
        terminal.Status = TerminalEnrollmentStatus.Revoked;
        terminal.RevokedAt = e.RevokedAt;
        terminal.ActiveStaffingSessionId = null;
    }

    public void Apply(TerminalStaffingSessionActivated e, TerminalEnrollment terminal)
        => terminal.ActiveStaffingSessionId = e.StaffingSessionId;

    public void Apply(TerminalStaffingSessionCleared e, TerminalEnrollment terminal)
    {
        // Only clear when the pointer still belongs to the ending session — a
        // supersede may already have activated a newer one.
        if (terminal.ActiveStaffingSessionId == e.StaffingSessionId)
            terminal.ActiveStaffingSessionId = null;
    }
}
