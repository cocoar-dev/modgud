using JasperFx.Events.Projections;
using Marten;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.Persistence.Marten.Projections.FunctionTerminals;

namespace Modgud.Infrastructure.FunctionTerminals;

/// <summary>
/// Marten wiring for the function-terminals slice (MG-FT work-item series):
/// documents, inline projections, and stable event-type aliases. Grows with the
/// series (TerminalEnrollment, FunctionStaffingCeremony, StaffingSession follow
/// in MG-FT-03/05).
/// </summary>
public static class FunctionTerminalsMartenSetup
{
    public static StoreOptions UseModgudFunctionTerminals(this StoreOptions options)
    {
        // Grant documents — inline projection of the grant streams. Indexes per
        // plan §5.2: the per-function list, the per-user lookup, the status
        // filter, and the composite the uniqueness check runs on.
        options.Schema.For<FunctionActivationGrant>()
            .Identity(x => x.Id)
            .Index(x => x.FunctionPrincipalId)
            .Index(x => x.UserId)
            .Index(x => x.Status)
            .Index(x => new { x.FunctionPrincipalId, x.UserId });

        options.Projections.Add<FunctionActivationGrantProjection>(ProjectionLifecycle.Inline);

        // Terminal slots — inline projection of the enrollment streams. The
        // unique indexes are half of the 1:1 terminal↔client rule (the other
        // half is the function-terminal client invariant). NOTE deliberate
        // deviation from plan §5.2: no UseOptimisticConcurrency on the
        // document — it is projection-owned; the "at most one active session"
        // activation lock (MG-FT-05) will guard via the stream version
        // (append-time optimistic concurrency), not via a direct doc write.
        options.Schema.For<TerminalEnrollment>()
            .Identity(x => x.Id)
            .Index(x => x.FunctionPrincipalId)
            .Index(x => x.ClientId, x => x.IsUnique = true)
            .Index(x => x.OAuthApplicationId, x => x.IsUnique = true)
            .Index(x => x.Status)
            .Index(x => x.ActiveStaffingSessionId);

        options.Projections.Add<TerminalEnrollmentProjection>(ProjectionLifecycle.Inline);

        // Terminal-consent tickets — ephemeral single-use documents (like the
        // passkey ceremonies), ExpiresAt indexed for opportunistic pruning.
        options.Schema.For<TerminalEnrollmentVerificationTicket>()
            .Identity(x => x.Id)
            .Index(x => x.ExpiresAt);

        // Staffing ceremonies — ephemeral single-use documents (plan §4.4).
        // Optimistic concurrency makes the consume (set ConsumedAt) race-safe:
        // of two racing redeems only one save wins (plan §13.3 step 6).
        options.Schema.For<FunctionStaffingCeremony>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.ExpiresAt)
            .Index(x => x.ClientId);

        // Staffing sessions — inline projection of the session streams
        // (plan §4.5/§5.2). OAuthAuthorizationId unique: one authorization is
        // one session's anchor, never shared.
        options.Schema.For<StaffingSession>()
            .Identity(x => x.Id)
            .Index(x => x.TerminalEnrollmentId)
            .Index(x => x.FunctionPrincipalId)
            .Index(x => x.ActivatedByUserId)
            .Index(x => x.ActivatedByPasskeyCredentialId)
            .Index(x => x.FunctionActivationGrantId)
            .Index(x => x.OAuthAuthorizationId, x => x.IsUnique = true)
            .Index(x => x.Status)
            .Index(x => x.AbsoluteExpiresAt);

        options.Projections.Add<StaffingSessionProjection>(ProjectionLifecycle.Inline);

        // Stable event-type aliases — keeps mt_events.type rename-proof.
        options.Events.MapEventType<FunctionActivationGrantIssued>("function_activation_grant_issued");
        options.Events.MapEventType<FunctionActivationGrantSuspended>("function_activation_grant_suspended");
        options.Events.MapEventType<FunctionActivationGrantResumed>("function_activation_grant_resumed");
        options.Events.MapEventType<FunctionActivationGrantRevoked>("function_activation_grant_revoked");

        options.Events.MapEventType<TerminalEnrollmentCreated>("terminal_enrollment_created");
        options.Events.MapEventType<TerminalEnrollmentDetailsChanged>("terminal_enrollment_details_changed");
        options.Events.MapEventType<TerminalEnrollmentEnrolled>("terminal_enrollment_enrolled");
        options.Events.MapEventType<TerminalEnrollmentDisabled>("terminal_enrollment_disabled");
        options.Events.MapEventType<TerminalEnrollmentReactivated>("terminal_enrollment_reactivated");
        options.Events.MapEventType<TerminalEnrollmentRevoked>("terminal_enrollment_revoked");
        options.Events.MapEventType<TerminalStaffingSessionActivated>("terminal_staffing_session_activated");
        options.Events.MapEventType<TerminalStaffingSessionCleared>("terminal_staffing_session_cleared");

        options.Events.MapEventType<StaffingSessionStarted>("staffing_session_started");
        options.Events.MapEventType<StaffingSessionEnded>("staffing_session_ended");

        return options;
    }
}
