using Marten;
using Microsoft.Extensions.Hosting;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.HealthChecks;

/// <summary>Dead letters per realm at one point in time.</summary>
public sealed record DeadLetterSnapshot(
    DateTimeOffset SampledAt,
    IReadOnlyDictionary<string, int> PerRealm,
    IReadOnlyList<string> Unreadable)
{
    public int Total => PerRealm.Values.Sum();
}

/// <summary>
/// Counts the Wolverine dead letters (<c>wolverine_dead_letters</c>) of every active realm
/// database in the background, so <see cref="DeadLetterHealthCheck"/> answers from a snapshot
/// and readiness never pays O(realms). A dead letter is a message that failed for good — for a
/// follow-up (<see cref="Modgud.Infrastructure.FollowUps.IFollowUpMessage"/>) an effect such as a
/// revocation that did not happen.
/// </summary>
public sealed class DeadLetterMonitor(IGlobalStore globalStore, IDocumentStore store, ILogger<DeadLetterMonitor> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private volatile DeadLetterSnapshot? _latest;

    public DeadLetterSnapshot? Latest => _latest;

    public async Task<DeadLetterSnapshot> SampleAsync(CancellationToken ct)
    {
        List<string> realms;
        await using (var global = globalStore.QuerySession())
            realms = [.. (await global.Query<Realm>().Where(r => r.IsActive).ToListAsync(ct)).Select(r => r.Slug)];

        var perRealm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        foreach (var realm in realms)
        {
            try
            {
                await using var session = store.QuerySession(realm);
                var schemas = await session.QueryAsync<string>(
                    "select table_schema from information_schema.tables where table_name = 'wolverine_dead_letters'", ct);
                var count = 0;
                foreach (var schema in schemas)
                {
                    var rows = await session.QueryAsync<int>(
                        $"select count(*)::int from \"{schema.Replace("\"", "\"\"")}\".wolverine_dead_letters", ct);
                    count += rows.Single();
                }
                if (count > 0) perRealm[realm] = count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dead letters of realm {Realm} could not be read", realm);
                unreadable.Add(realm);
            }
        }

        var snapshot = new DeadLetterSnapshot(DateTimeOffset.UtcNow, perRealm, unreadable);
        _latest = snapshot;
        if (snapshot.Total > 0)
            logger.LogWarning("{Total} dead letter(s) waiting: {Realms}", snapshot.Total, string.Join(", ", perRealm.Select(p => $"{p.Key}={p.Value}")));
        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup and realm bootstrap finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await SampleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Dead-letter sampling failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
