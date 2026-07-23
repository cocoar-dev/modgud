using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// End-to-end Marten readiness probe: opens the global store and runs a no-op
/// query against the Realm directory. If the Marten schema isn't applied or
/// the master DB connection string is wrong, this fails — which is exactly
/// what readiness should refuse traffic for.
///
/// <para>Per-tenant DBs are deliberately NOT probed here — they're
/// initialised on-demand and the count grows over time. Probing each
/// would make readiness latency O(realms).</para>
/// </summary>
public sealed class MartenSchemaHealthCheck(IGlobalStore globalStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = globalStore.QuerySession();
            var _ = await session.Query<Realm>().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy("Marten master schema reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Marten master schema check failed — migrations may be pending.",
                ex);
        }
    }
}
