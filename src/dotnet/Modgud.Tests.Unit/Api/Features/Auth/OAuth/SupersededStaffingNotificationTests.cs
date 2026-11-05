using Modgud.Api.Features.Auth.OAuth;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Tests.Unit.Api.Features.Auth.OAuth;

/// <summary>
/// GS-CO-01 — a tap on a multi-position terminal can staff position B while position A's
/// session is still active there. The end notification for A's replaced session must name
/// A, the position that session staffed, not the position of the new tap.
/// </summary>
public sealed class SupersededStaffingNotificationTests
{
    [Fact]
    public void The_end_of_a_replaced_session_names_that_sessions_own_position_and_terminal()
    {
        var positionA = Guid.NewGuid();
        var terminal = Guid.NewGuid();
        var superseded = new StaffingSession
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = positionA,
            TerminalEnrollmentId = terminal,
        };
        var now = DateTimeOffset.UtcNow;

        var ended = AuthorizationEndpoints.SupersededSessionEnded(superseded, now);

        Assert.Equal(positionA, ended.PositionPrincipalId);
        Assert.Equal(terminal, ended.TerminalEnrollmentId);
        Assert.Equal(superseded.Id, ended.StaffingSessionId);
        Assert.Equal(StaffingSessionEndReason.ReplacedByNewActivation, ended.Reason);
        Assert.Equal(now, ended.EndedAt);
    }
}
