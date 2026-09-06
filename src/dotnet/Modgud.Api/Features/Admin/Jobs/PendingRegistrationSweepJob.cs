using Modgud.Authentication.Registration;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.RateLimiting;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// ADR 0018 — hourly hard-delete of expired (and any crash-orphaned consumed) pending
/// registrations. A pending record is a stranger's unverified input; once its proof can
/// no longer succeed nothing may remain of it. Per realm, idempotent, cheap (indexed on
/// <c>ExpiresAt</c>).
/// </summary>
[DisallowConcurrentExecution]
public class PendingRegistrationSweepJob(
    IRegistrationPipeline pipeline,
    IRateLimitStore rateLimits,
    Modgud.Authentication.Devices.IDeviceTrust devices) : IJob
{
    public const string Key = "pending-registration-sweep";
    public const string Name = "Pending registration sweep";
    public const string Description =
        "Hard-deletes expired pending registrations (sign-ups whose proof was never completed) and " +
        "prunes rate-limit counters idle for two days and drops trusted-device records idle for 90 days. " +
        "Nothing identifying the person remains afterwards. " +
        "Per-realm, idempotent.";

    /// <summary>Ten past every hour.</summary>
    public const string DefaultCron = "0 10 * * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var swept = await pipeline.SweepAsync(context.CancellationToken);
        // ADR 0019 — counters are keyed by address / mailbox / client; drop the ones nobody
        // touched for two days so the table never grows with one-off sources.
        var pruned = await rateLimits.PruneAsync(
            new RateLimitScope(TenantContext.Current), DateTimeOffset.UtcNow.AddDays(-2), context.CancellationToken);
        // ADR 0020 — a device nobody logged in from for 90 days is forgotten.
        var devicesSwept = await devices.SweepAsync(
            DateTimeOffset.UtcNow - Modgud.Authentication.Devices.TrustedDevice.IdleLifetime, context.CancellationToken);
        context.Result = (swept == 0 ? "No expired pending registrations" : $"Deleted {swept} pending registration(s)")
                         + (pruned == 0 ? "" : $"; pruned {pruned} idle rate-limit counter(s)")
                         + (devicesSwept == 0 ? "" : $"; forgot {devicesSwept} idle trusted device(s)");
    }
}
