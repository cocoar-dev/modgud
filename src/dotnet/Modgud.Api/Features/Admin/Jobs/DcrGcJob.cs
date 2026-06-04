using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Modgud.Application.Dcr;
using Modgud.Application.Scheduling;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Daily sweep that soft-deletes DCR clients whose
/// <c>cocoar:dcr:last_used_at</c> has aged past the realm's configured TTL
/// (default 90 days, per-realm override via <c>RealmSettings.Dcr.GcTtlDays</c>).
///
/// <para>Migrated from the standalone <c>DcrGarbageCollectorService</c>
/// hosted-service in Phase 1A of the Quartz adoption. The logic is unchanged;
/// the wrapper is now the standard <see cref="IJob"/> contract so the run
/// shows up in the admin Jobs UI with manual-trigger + history.</para>
///
/// <para>Soft delete (via <c>OAuthApplicationAggregate.Delete</c>) keeps
/// audit-log entries intact and preserves <c>client_id</c> history for
/// forensics. Realms with DCR disabled OR with no DCR clients end the
/// inner loop after a single indexed query — cheap, predictable.</para>
/// </summary>
[DisallowConcurrentExecution]
public class DcrGcJob(
    IServiceScopeFactory scopeFactory,
    ISecurityAuditLog securityAudit) : IJob
{
    public const string Key = "dcr-gc";
    public const string Name = "DCR Garbage Collector";
    public const string Description =
        "Soft-deletes Dynamic-Client-Registration clients whose last-used-at has aged past " +
        "the realm's configured TTL (RealmSettings.Dcr.GcTtlDays, default 90 days). " +
        "Per-realm, idempotent.";

    /// <summary>04:00 UTC daily — after history-retention.</summary>
    public const string DefaultCron = "0 0 4 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        using var rootScope = scopeFactory.CreateScope();
        var store = rootScope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Realms live in the master DB. The control-plane realm uses the
        // "system" tenant; tenant realms use their slug.
        await using var masterSession = store.LightweightSession("system");
        var realms = await masterSession.Query<Realm>()
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        int realmsTouched = 0;
        int totalSwept = 0;
        foreach (var realm in realms)
        {
            if (ct.IsCancellationRequested) break;
            var swept = await SweepRealmAsync(store, realm.Slug, ct);
            if (swept >= 0)
            {
                realmsTouched++;
                totalSwept += swept;
            }
        }

        context.Result = totalSwept == 0
            ? $"No DCR clients aged out ({realmsTouched} realm(s) checked)"
            : $"Soft-deleted {totalSwept} DCR client(s) across {realmsTouched} realm(s)";
    }

    /// <summary>Returns swept count, or -1 if the realm was skipped (DCR disabled).</summary>
    private async Task<int> SweepRealmAsync(IDocumentStore store, string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);

        var settings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var dcr = settings?.Dcr;
        if (dcr is null || !dcr.Enabled) return -1;

        // Find DCR clients in the realm (the IsDynamicallyRegistered marker
        // lives in the Properties dict, which Marten LINQ can't efficiently
        // match — pull the candidates and filter in memory. Set size is
        // bounded by the realm-rate-limit (default 100/d × TTL=90d = 9000
        // max-ever), tiny enough for an in-memory pass).
        var candidates = await session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted)
            .ToListAsync(ct);

        var ttl = TimeSpan.FromDays(dcr.GcTtlDays);
        var cutoff = DateTimeOffset.UtcNow - ttl;
        var swept = 0;

        foreach (var state in candidates)
        {
            if (!IsDcrClient(state.Properties)) continue;
            var lastUsedAt = ParseTimestamp(state.Properties, OAuthApplicationPropertyKeys.DcrLastUsedAt);
            if (lastUsedAt is null || lastUsedAt > cutoff) continue;

            var aggregate = await session.Events
                .AggregateStreamAsync<OAuthApplicationAggregate>(state.Id, token: ct);
            if (aggregate is null || aggregate.IsDeleted) continue;

            session.Events.Append(state.Id, aggregate.Delete());
            swept++;

            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.DcrClientGarbageCollected,
                Realm = tenantId,
                Level = "Info",
                Status = "collected",
                Reason = $"clientId {state.ClientId}, ttl {dcr.GcTtlDays}d",
                Message = $"DCR client garbage-collected: {state.ClientId}",
            });
        }

        if (swept > 0)
        {
            await session.SaveChangesAsync(ct);
        }
        return swept;
    }

    private static bool IsDcrClient(IDictionary<string, object?> props)
    {
        if (!props.TryGetValue(OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered, out var raw) || raw is null)
            return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            _ => false,
        };
    }

    private static DateTimeOffset? ParseTimestamp(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return null;
        var str = raw switch
        {
            string s => s,
            JsonElement e when e.ValueKind is JsonValueKind.String => e.GetString(),
            _ => null,
        };
        return DateTimeOffset.TryParse(str, out var dt) ? dt : null;
    }
}
