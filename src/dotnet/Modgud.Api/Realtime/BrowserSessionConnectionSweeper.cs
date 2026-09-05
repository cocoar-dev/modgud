using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Sessions;

namespace Modgud.Api.Realtime;

/// <summary>
/// Re-validates every browser session that holds a SignalR connection on this
/// node against the database and aborts the connections of sessions that are
/// gone or expired (ADR 0010, D6).
/// <para>
/// A revocation is processed on one node; the connection may live on another.
/// Each hub invocation already re-checks the session row, so an active client
/// is cut off at its next call. An idle client (an open admin grid that only
/// receives) would keep its socket until the next push, which is why this
/// sweep exists: it closes that window to <see cref="Interval"/> without any
/// cross-node message. On a single node it is a cheap no-op in practice —
/// the local registry already aborted the connection synchronously.
/// </para>
/// </summary>
public sealed class BrowserSessionConnectionSweeper(
    IBrowserSessionConnectionRegistry registry,
    IDocumentStore store,
    TimeProvider clock,
    ILogger<BrowserSessionConnectionSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Browser-session connection sweep failed; retrying at the next tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // host shutdown
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var snapshot = registry.Snapshot();
        if (snapshot.Count == 0) return;

        var now = clock.GetUtcNow();
        foreach (var byRealm in snapshot.GroupBy(c => c.Realm))
        {
            if (string.IsNullOrEmpty(byRealm.Key))
            {
                // No realm on the connection means the session can never be
                // validated — those connections should not exist; drop them.
                foreach (var c in byRealm) registry.Revoke(c.SessionId);
                continue;
            }

            var ids = byRealm.Select(c => c.SessionId).ToArray();
            HashSet<Guid> alive;
            await using (var session = store.QuerySession(byRealm.Key))
            {
                var rows = await session.Query<UserSession>()
                    .Where(s => s.Id.IsOneOf(ids))
                    .ToListAsync(ct);
                alive = rows.Where(s => s.IsActive(now)).Select(s => s.Id).ToHashSet();
            }

            foreach (var id in ids.Where(id => !alive.Contains(id)))
            {
                logger.LogInformation(
                    "Browser session {SessionId} in realm {Realm} is no longer valid — aborting its connections on this node",
                    id, byRealm.Key);
                registry.Revoke(id);
            }
        }
    }
}
