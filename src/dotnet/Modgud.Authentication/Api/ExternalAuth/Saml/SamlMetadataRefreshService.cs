using Modgud.Infrastructure.Audit;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Background service that periodically re-fetches IdP federation metadata
/// for every registered SAML provider per the per-provider cadence
/// (<see cref="Identity.LoginProviders.Saml.SamlFlavorData.MetadataRefreshIntervalSeconds"/>,
/// default 24h). Picks up IdP cert rotations before they break logins —
/// most IdPs advertise the next signing key in metadata 1-2 weeks before
/// activating it.
/// <para>
/// Polls every <see cref="PollInterval"/> and refreshes any provider whose
/// last-fetched-at is older than its configured cadence. Failures are
/// logged + stale data is kept; the cache never goes empty just because a
/// refresh failed.
/// </para>
/// </summary>
public class SamlMetadataRefreshService(
    DynamicSamlSchemeManager manager,
    TimeProvider clock,
    ISecurityAuditLog securityAudit,
    ILogger<SamlMetadataRefreshService> logger) : BackgroundService
{
    /// <summary>
    /// How often we wake up and check whether anything is due. Tighter than
    /// the typical 24h refresh cadence so providers configured with a 1h
    /// override still see roughly-on-time refreshes.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so we don't fight startup / bootstrap work at
        // process launch — the bootstrap already fetched fresh metadata
        // moments ago.
        try { await Task.Delay(PollInterval, clock, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SAML metadata refresh tick failed unexpectedly — continuing");
            }

            try { await Task.Delay(PollInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var snapshot = manager.GetAllRegistered();
        var refreshed = 0;
        var failed = 0;

        foreach (var entry in snapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsDue(entry, now)) continue;

            try
            {
                var ok = await manager.RefreshMetadataAsync(entry.LoginProviderId, ct);
                if (ok) refreshed++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex,
                    "SAML metadata refresh failed for provider {Id}",
                    entry.LoginProviderId);
            }
        }

        if (refreshed > 0 || failed > 0)
        {
            securityAudit.RecordPlatformTelemetry(new PlatformAuditRecord
            {
                EventType = AuditEvents.SamlMetadataRefreshCompleted,
                OutcomeCode = failed == 0 ? AuditOutcomes.Succeeded : AuditOutcomes.Completed,
                ReasonCode = failed == 0 ? null : "partial-failure",
                OperationCode = "refresh-due-providers",
                Count = refreshed,
                RelatedCount = failed,
            });
        }
    }

    private static bool IsDue(RegisteredSamlProvider entry, DateTimeOffset now)
    {
        // Never-fetched providers are always due — initial bootstrap may
        // have left them empty if the metadata URL was unreachable then.
        if (entry.MetadataFetchedAt is null) return true;

        var cadence = TimeSpan.FromSeconds(Math.Max(60, entry.FlavorData.MetadataRefreshIntervalSeconds));
        return entry.MetadataFetchedAt.Value.Add(cadence) <= now;
    }
}
