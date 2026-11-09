using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// Reports <see cref="HealthStatus.Degraded"/> while a realm has Wolverine dead letters — messages
/// that failed for good, such as a follow-up revocation or mail — or while a realm's dead letters
/// cannot be read. Degraded keeps readiness at 200 (traffic is not the problem) but shows up in
/// the readiness JSON and in monitoring. Answers from <see cref="DeadLetterMonitor"/>'s snapshot.
/// </summary>
public sealed class DeadLetterHealthCheck(DeadLetterMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = monitor.Latest;
        if (snapshot is null)
            return Task.FromResult(HealthCheckResult.Healthy("Dead letters not sampled yet."));

        var data = snapshot.PerRealm.ToDictionary(p => p.Key, p => (object)p.Value);
        if (snapshot.Total > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{snapshot.Total} dead letter(s) need attention: "
                + string.Join(", ", snapshot.PerRealm.Select(p => $"{p.Key} {p.Value}"))
                + $" (sampled {snapshot.SampledAt:u}).",
                data: data));
        }
        if (snapshot.Unreadable.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Dead letters could not be read for: " + string.Join(", ", snapshot.Unreadable) + "."));
        }
        return Task.FromResult(HealthCheckResult.Healthy($"No dead letters (sampled {snapshot.SampledAt:u})."));
    }
}
