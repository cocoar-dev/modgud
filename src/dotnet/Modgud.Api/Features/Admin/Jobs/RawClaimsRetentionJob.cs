using Modgud.Authentication.Api.ExternalAuth;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// EP-03 — enforces each login provider's raw-claims retention in this realm: expired raw
/// IdP claims are removed from the external-identity links and from their login events.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RawClaimsRetentionJob(RawClaimsRetentionSweeper sweeper) : IJob
{
    public const string Key = "raw-claims-retention";
    public const string Name = "Raw Claims Retention";
    public const string Description =
        "Removes raw identity-provider claims older than their login provider's retention from " +
        "external-identity links and their login events. Email, display name and script output stay. " +
        "Providers without a retention are skipped. Per-realm, idempotent.";

    /// <summary>03:45 UTC daily.</summary>
    public const string DefaultCron = "0 45 3 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var result = await sweeper.SweepAsync(context.CancellationToken);
        context.Result = result.Links == 0 && result.Events == 0
            ? "No expired raw claims"
            : $"Cleared raw claims on {result.Links} link(s) and in {result.Events} login event(s)";
    }
}
