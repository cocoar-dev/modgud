using Modgud.Authentication.Registration;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// ADR 0006 — hourly hard-delete of expired (and any crash-orphaned consumed) pending
/// registrations. A pending record is a stranger's unverified input; once its proof can
/// no longer succeed nothing may remain of it. Per realm, idempotent, cheap (indexed on
/// <c>ExpiresAt</c>).
/// </summary>
[DisallowConcurrentExecution]
public class PendingRegistrationSweepJob(IRegistrationPipeline pipeline) : IJob
{
    public const string Key = "pending-registration-sweep";
    public const string Name = "Pending registration sweep";
    public const string Description =
        "Hard-deletes expired pending registrations (sign-ups whose proof was never completed). " +
        "Nothing identifying the person remains afterwards. Per-realm, idempotent.";

    /// <summary>Ten past every hour.</summary>
    public const string DefaultCron = "0 10 * * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var swept = await pipeline.SweepAsync(context.CancellationToken);
        context.Result = swept == 0 ? "No expired pending registrations" : $"Deleted {swept} pending registration(s)";
    }
}
