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

        // Stable event-type aliases — keeps mt_events.type rename-proof.
        options.Events.MapEventType<FunctionActivationGrantIssued>("function_activation_grant_issued");
        options.Events.MapEventType<FunctionActivationGrantSuspended>("function_activation_grant_suspended");
        options.Events.MapEventType<FunctionActivationGrantResumed>("function_activation_grant_resumed");
        options.Events.MapEventType<FunctionActivationGrantRevoked>("function_activation_grant_revoked");

        return options;
    }
}
