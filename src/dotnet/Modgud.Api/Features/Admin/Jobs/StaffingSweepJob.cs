using Marten;
using Quartz;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.FunctionTerminals;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// MG-FT-07 (plan §5.3) — the function-terminals janitor. Two duties per realm:
/// <list type="number">
///   <item><description>Delete lapsed <see cref="FunctionStaffingCeremony"/>
///   documents (begun but never redeemed; the begin endpoint also prunes
///   amortized — this is the backstop for idle realms).</description></item>
///   <item><description>End active <see cref="StaffingSession"/>s whose
///   <c>AbsoluteExpiresAt</c> has passed, through the revoker (Ended/Expired
///   event, terminal pointer, authorization revoke, audit). The staffing
///   refresh already lazy-expires on contact; this catches sessions whose
///   terminal simply went silent.</description></item>
/// </list>
/// Ended sessions are deliberately NOT deleted here — they are audit records
/// with their own retention story (plan §5.3).
/// </summary>
[DisallowConcurrentExecution]
public class StaffingSweepJob(
    IDocumentSession session,
    IFunctionStaffingRevoker staffingRevoker,
    AppSettings settings) : IJob
{
    public const string Key = "staffing-sweep";
    public const string Name = "Function Staffing Sweep";
    public const string Description =
        "Deletes lapsed function-staffing ceremonies and ends staffing sessions that ran past " +
        "their absolute end (the shift ceiling a refresh can never move). Per-realm, idempotent.";

    /// <summary>Every 5 minutes — a silent terminal's session should not
    /// outlive its shift ceiling by more than a sweep interval.</summary>
    public const string DefaultCron = "0 */5 * * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        if (!settings.Features.FunctionTerminals)
        {
            context.Result = "Skipped because function terminals are disabled";
            return;
        }

        var ct = context.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        session.DeleteWhere<FunctionStaffingCeremony>(c => c.ExpiresAt < now);
        await session.SaveChangesAsync(ct);

        var expired = (await session.Query<StaffingSession>()
            .Where(s => s.Status == StaffingSessionStatus.Active && s.AbsoluteExpiresAt <= now)
            .ToListAsync(ct))
            .Select(s => s.Id)
            .ToList();

        var ended = 0;
        foreach (var id in expired)
        {
            ended += await staffingRevoker.EndSessionAsync(id, StaffingSessionEndReason.Expired, ct);
        }

        context.Result = ended switch
        {
            0 => "No staffing sessions ran past their absolute end",
            _ => $"Ended {ended} expired staffing session(s)",
        };
    }
}
