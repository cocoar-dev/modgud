using System.Text.Json;
using Cocoar.Auth.Application.Dcr;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RealmSettingsDoc = Cocoar.Auth.Domain.RealmSettings.RealmSettings;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Daily background sweep that soft-deletes DCR clients whose
/// <c>cocoar:dcr:last_used_at</c> has aged past the realm's configured
/// TTL (default 90 days, per-realm override via
/// <c>RealmSettings.Dcr.GcTtlDays</c>).
///
/// <para>Soft delete (via <see cref="OAuthApplicationAggregate.Delete"/>)
/// keeps the audit-log entries intact and preserves the
/// <c>client_id</c> history for forensics. The application's row in
/// the inline projection is flagged <c>IsDeleted=true</c> so the
/// runtime stops resolving it; subsequent token issues for the
/// dead <c>client_id</c> return the usual "client not found"
/// rejection.</para>
///
/// <para>Tenant iteration: the master DB carries the realm catalog
/// (one <c>Realm</c> doc per tenant). The service opens a
/// per-realm tenanted session on each pass, so a per-realm TTL
/// override resolves correctly. Realms with DCR disabled OR with no
/// DCR clients yet end the inner loop after a single indexed query —
/// cheap, predictable.</para>
///
/// <para>Schedule: 24 h interval, fires once at startup to clean up
/// anything that aged out while the service was offline. The wide
/// window is fine because GC isn't load-bearing — LastUsedAt drives
/// the cutoff, not wall-clock.</para>
/// </summary>
public sealed class DcrGarbageCollectorService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _services;
    private readonly ILogger<DcrGarbageCollectorService> _logger;

    public DcrGarbageCollectorService(IServiceProvider services, ILogger<DcrGarbageCollectorService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "DCR garbage-collector sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Realms live in the master DB. The control-plane realm uses
        // the "system" tenant; tenant realms use their slug.
        await using var masterSession = store.LightweightSession("system");
        var realms = await masterSession.Query<Realm>()
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        foreach (var realm in realms)
        {
            if (ct.IsCancellationRequested) return;
            await SweepRealmAsync(store, realm.Slug, ct);
        }
    }

    private async Task SweepRealmAsync(IDocumentStore store, string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);

        var settings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var dcr = settings?.Dcr;
        if (dcr is null || !dcr.Enabled) return;

        // Find DCR clients in the realm (the IsDynamicallyRegistered
        // marker lives in the Properties dict, which Marten LINQ can't
        // efficiently match — pull the candidates and filter in memory.
        // Set size is bounded by the realm-rate-limit (default 100/d ×
        // TTL=90d = 9000 max-ever), tiny enough for an in-memory pass).
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

            var registeredAt = ParseTimestamp(state.Properties, OAuthApplicationPropertyKeys.DcrRegisteredAt);
            _logger.LogInformation(
                "Auth: " + DcrAuditEvents.ClientGarbageCollected +
                " ClientId={ClientId} RegisteredAt={RegisteredAt} LastUsedAt={LastUsedAt} TtlDays={TtlDays} Realm={Realm}",
                state.ClientId, registeredAt, lastUsedAt, dcr.GcTtlDays, tenantId);
        }

        if (swept > 0)
        {
            await session.SaveChangesAsync(ct);
        }
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
