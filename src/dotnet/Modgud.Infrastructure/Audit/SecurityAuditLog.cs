using System.Threading.Channels;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Audit;

/// <summary>
/// In-process implementation of <see cref="ISecurityAuditLog"/>: a bounded channel
/// that <see cref="SecurityAuditWriter"/> drains to the system DB.
///
/// <para><b>Bounded + drop-on-full</b> (the legacy sink was an UNBOUNDED channel —
/// a memory-growth risk under a credential-stuffing storm). When the writer can't
/// keep up the oldest behaviour we want is to shed load, never to block the auth
/// path or grow without limit. Dropped counts are exposed for the writer to log.</para>
///
/// <para>The realm is captured HERE, on the calling (request) thread where
/// <c>TenantContext.Current</c> is set — the writer runs tenant-less in a
/// background service, exactly as <c>RealmLogEnricher</c> captured it for the
/// legacy sink. Category + control-plane visibility are derived from the event type
/// so the row can't disagree with the taxonomy.</para>
/// </summary>
public sealed class SecurityAuditLog : ISecurityAuditLog
{
    // Generous bound: a real burst is absorbed; a pathological flood sheds rather
    // than OOMs. SingleReader because exactly one SecurityAuditWriter drains it.
    private readonly Channel<SecurityAuditEntry> _channel =
        Channel.CreateBounded<SecurityAuditEntry>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private long _dropped;

    internal ChannelReader<SecurityAuditEntry> Reader => _channel.Reader;

    /// <summary>Total records dropped because the channel was full (read-and-reset
    /// by the writer so it can log bursts).</summary>
    internal long ReadAndResetDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Record(SecurityAuditRecord record)
    {
        var entry = new SecurityAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            // Explicit override wins (realm-iterating background jobs), else the
            // ambient realm — mirrors the legacy RealmLogEnricher dual-sourcing.
            Realm = record.Realm ?? TenantContext.Current,
            EventType = record.EventType,
            Category = AuditEvents.CategoryOf(record.EventType),
            PlatformOnly = AuditEvents.IsPlatformOnly(record.EventType),
            Level = record.Level,
            Actor = record.Actor,
            Ip = record.Ip,
            Status = record.Status,
            Reason = record.Reason,
            Message = record.Message,
        };

        if (!_channel.Writer.TryWrite(entry))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Synchronously drain everything currently queued to the system DB. For
    /// SHORT-LIVED process paths that never start the host (so
    /// <see cref="SecurityAuditWriter"/> never runs) — notably the recovery CLI and
    /// STARTUP_COMMAND. Without this, records those paths enqueue would be lost on
    /// exit, which is exactly the high-value break-glass forensic trail we must keep.
    /// Safe to call when the channel is empty (no-op). NOT used on the normal web
    /// path, where the background writer owns the drain.
    /// </summary>
    public async Task FlushAsync(IDocumentStore store, CancellationToken ct = default)
    {
        var batch = new List<SecurityAuditEntry>();
        while (_channel.Reader.TryRead(out var entry))
            batch.Add(entry);

        if (batch.Count == 0)
            return;

        await using var session = store.LightweightSession(TenantConstants.SystemTenantId);
        session.Store(batch.ToArray());
        await session.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Background service that drains <see cref="SecurityAuditLog"/> into the system DB
/// in batches. Replaces the legacy <c>AuthLogPersistenceService</c> drain loop; the
/// retention prune that lived there is now a separate Quartz job over this store.
/// </summary>
public sealed class SecurityAuditWriter(
    IServiceProvider services,
    SecurityAuditLog log,
    ILogger<SecurityAuditWriter> logger) : BackgroundService
{
    private const int MaxBatch = 256;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = log.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<SecurityAuditEntry>(MaxBatch);
            while (batch.Count < MaxBatch && reader.TryRead(out var entry))
                batch.Add(entry);

            if (batch.Count == 0)
                continue;

            try
            {
                using var scope = services.CreateScope();
                // Runs out-of-band in a HostedService — no HttpContext to drive
                // tenant resolution, so target the system tenant explicitly. The
                // streamless store lives cross-realm in the system DB by design;
                // each row already carries its own Realm captured at emit time.
                await using var session = scope.ServiceProvider
                    .GetRequiredService<IDocumentStore>()
                    .LightweightSession(TenantConstants.SystemTenantId);

                session.Store(batch.ToArray());
                await session.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to persist {Count} security audit entries", batch.Count);
            }

            var dropped = log.ReadAndResetDropped();
            if (dropped > 0)
                logger.LogWarning("Security audit store shed {Dropped} record(s) — channel full", dropped);
        }
    }
}
