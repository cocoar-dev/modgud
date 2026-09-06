using Modgud.Authentication.BackChannelLogout;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// ADR 0021 — retries logout-token deliveries whose immediate attempt failed, on the
/// schedule in <see cref="BackChannelLogoutConstants.RetrySchedule"/>, and gives up after
/// the last step (the change feed carries the same fact). Per realm, idempotent: a
/// delivery is claimed with optimistic concurrency, so the job and the in-process first
/// attempt never send the same token twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class BackChannelLogoutRetryJob(IBackChannelLogoutDeliverer deliverer) : IJob
{
    public const string Key = "backchannel-logout-retry";
    public const string Name = "Back-channel logout retry";
    public const string Description =
        "Retries logout-token deliveries to relying parties whose logout URI did not accept the first attempt " +
        "(after about 1, 5 and 30 minutes), then gives up. Per-realm, idempotent.";

    /// <summary>Every minute.</summary>
    public const string DefaultCron = "0 * * * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var (attempted, delivered) = await deliverer.SweepDueAsync(context.CancellationToken);
        context.Result = attempted == 0
            ? "No due deliveries"
            : $"Attempted {attempted} delivery(ies), {delivered} delivered";
    }
}
