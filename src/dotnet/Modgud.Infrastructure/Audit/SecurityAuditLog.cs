using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Infrastructure.Audit;

/// <summary>
/// Classified streamless audit sink. Required and incident records are written
/// synchronously before the caller can report success/rejection. Abuse records
/// use a bounded aggregating buffer, and reconstructable telemetry uses the same
/// buffer with an explicit best-effort contract.
/// </summary>
public sealed class SecurityAuditLog(
    IHttpContextAccessor httpContextAccessor,
    IServiceScopeFactory scopeFactory) : ISecurityAuditLog
{
    private readonly Channel<SecurityAuditEnvelope> _channel =
        Channel.CreateBounded<SecurityAuditEnvelope>(new BoundedChannelOptions(50_000)
        {
            // We intentionally use TryWrite below. Wait mode makes TryWrite
            // return false when full, allowing us to count the shed raw
            // occurrence precisely; DropWrite reports acceptance even when it
            // discards the item.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private long _dropped;

    internal ChannelReader<SecurityAuditEnvelope> Reader => _channel.Reader;
    internal long ReadAndResetDropped() => Interlocked.Exchange(ref _dropped, 0);

    public ValueTask RecordRequiredAsync(
        SecurityAuditRecord record,
        CancellationToken ct = default)
        => PersistRealmNowAsync(record, AuditDurabilityClass.Required, ct);

    public void StoreRequired(
        IDocumentSession session,
        SecurityAuditRecord record)
    {
        EnsureClass(record.EventType, AuditDurabilityClass.Required);
        var envelope = CaptureRealmEnvelope(record, AuditDurabilityClass.Required);
        SecurityAuditPersistence.StoreRequired(session, envelope);
    }

    public ValueTask RecordIncidentAsync(
        SecurityAuditRecord record,
        CancellationToken ct = default)
        => PersistRealmNowAsync(record, AuditDurabilityClass.Incident, ct);

    public void RecordAbuse(SecurityAuditRecord record)
        => EnqueueRealm(record, AuditDurabilityClass.Abuse);

    public void RecordTelemetry(SecurityAuditRecord record)
        => EnqueueRealm(record, AuditDurabilityClass.Telemetry);

    public ValueTask RecordPlatformRequiredAsync(
        PlatformAuditRecord record,
        CancellationToken ct = default)
        => PersistPlatformNowAsync(record, AuditDurabilityClass.Required, ct);

    public void StorePlatformRequired(
        IDocumentSession session,
        PlatformAuditRecord record)
    {
        EnsureClass(record.EventType, AuditDurabilityClass.Required);
        SecurityAuditPersistence.StorePlatformRequired(
            session,
            SecurityAuditEnvelope.ForPlatform(
                record with
                {
                    CorrelationId = record.CorrelationId
                        ?? CurrentCorrelationId(httpContextAccessor.HttpContext),
                },
                AuditDurabilityClass.Required));
    }

    public void RecordPlatformTelemetry(PlatformAuditRecord record)
    {
        EnsureClass(record.EventType, AuditDurabilityClass.Telemetry);
        try
        {
            Enqueue(SecurityAuditEnvelope.ForPlatform(
                record with
                {
                    CorrelationId = record.CorrelationId
                        ?? CurrentCorrelationId(httpContextAccessor.HttpContext),
                },
                AuditDurabilityClass.Telemetry));
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async ValueTask PersistRealmNowAsync(
        SecurityAuditRecord record,
        AuditDurabilityClass expected,
        CancellationToken ct)
    {
        EnsureClass(record.EventType, expected);
        var envelope = CaptureRealmEnvelope(record, expected);
        using var scope = scopeFactory.CreateScope();
        await SecurityAuditPersistence.PersistAsync(
            [envelope],
            scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
            scope.ServiceProvider.GetRequiredService<IGlobalStore>(),
            ct);
    }

    private async ValueTask PersistPlatformNowAsync(
        PlatformAuditRecord record,
        AuditDurabilityClass expected,
        CancellationToken ct)
    {
        EnsureClass(record.EventType, expected);
        var envelope = SecurityAuditEnvelope.ForPlatform(
            record with
            {
                CorrelationId = record.CorrelationId
                    ?? CurrentCorrelationId(httpContextAccessor.HttpContext),
            },
            expected);
        using var scope = scopeFactory.CreateScope();
        await SecurityAuditPersistence.PersistAsync(
            [envelope],
            scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
            scope.ServiceProvider.GetRequiredService<IGlobalStore>(),
            ct);
    }

    private void EnqueueRealm(
        SecurityAuditRecord record,
        AuditDurabilityClass expected)
    {
        EnsureClass(record.EventType, expected);
        try
        {
            Enqueue(CaptureRealmEnvelope(record, expected));
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private SecurityAuditEnvelope CaptureRealmEnvelope(
        SecurityAuditRecord record,
        AuditDurabilityClass durabilityClass)
    {
        var http = record.CaptureRequestContext
            ? httpContextAccessor.HttpContext
            : null;
        var subject = record.ActorSubjectId ??
            (record.ActorKind is null or AuditActorKind.User ? TryGetSubject(http) : null);
        var ip = record.IpAddress ?? http?.Connection.RemoteIpAddress?.ToString();
        var requestUserAgent = http?.Request.Headers.UserAgent.ToString();
        var userAgent = record.UserAgent ??
            (string.IsNullOrWhiteSpace(requestUserAgent) ? null : requestUserAgent);
        var actorKind = record.ActorKind
            ?? (subject is not null
                ? AuditActorKind.User
                : record.UnknownIdentifier is not null
                    ? AuditActorKind.AnonymousIdentifier
                    : record.OAuthClientId is not null
                        ? AuditActorKind.OAuthClient
                        : AuditActorKind.System);

        return SecurityAuditEnvelope.ForRealm(
            record.RealmSlug ?? TenantContext.Current,
            record with
            {
                ActorSubjectId = subject,
                IpAddress = ip,
                UserAgent = userAgent,
                CorrelationId = record.CorrelationId ?? CurrentCorrelationId(http),
                ActorKind = actorKind,
            },
            durabilityClass);
    }

    private void Enqueue(SecurityAuditEnvelope envelope)
    {
        if (!_channel.Writer.TryWrite(envelope))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Drains the buffer for short-lived recovery-CLI processes which never start
    /// the hosted writer.
    /// </summary>
    public async Task FlushAsync(
        IDocumentStore realmStore,
        IGlobalStore globalStore,
        CancellationToken ct = default)
    {
        var batch = new List<SecurityAuditEnvelope>();
        while (_channel.Reader.TryRead(out var entry))
            batch.Add(entry);

        if (batch.Count > 0)
        {
            var consolidated = SecurityAuditBatching.ConsolidateAbuse(batch);
            await SecurityAuditPersistence.PersistAsync(
                consolidated,
                realmStore,
                globalStore,
                ct);
        }
    }

    private static void EnsureClass(
        string eventType,
        AuditDurabilityClass expected)
    {
        var actual = AuditDurability.Classify(eventType);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Audit event '{eventType}' is classified as {actual}, not {expected}.");
        }
    }

    private static Guid? TryGetSubject(HttpContext? http)
    {
        var value = http?.User.FindFirst("sub")?.Value
            ?? http?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var subject) ? subject : null;
    }

    private static string? CurrentCorrelationId(HttpContext? http)
        => Activity.Current?.TraceId.ToString()
            ?? http?.TraceIdentifier;
}

internal sealed record SecurityAuditEnvelope
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public required AuditDurabilityClass DurabilityClass { get; init; }
    public required string? RealmSlug { get; init; }
    public SecurityAuditRecord? RealmRecord { get; init; }
    public PlatformAuditRecord? PlatformRecord { get; init; }

    public static SecurityAuditEnvelope ForRealm(
        string realmSlug,
        SecurityAuditRecord record,
        AuditDurabilityClass durabilityClass)
        => new()
        {
            RealmSlug = realmSlug,
            RealmRecord = record,
            DurabilityClass = durabilityClass,
        };

    public static SecurityAuditEnvelope ForPlatform(
        PlatformAuditRecord record,
        AuditDurabilityClass durabilityClass)
        => new()
        {
            RealmSlug = null,
            PlatformRecord = record,
            DurabilityClass = durabilityClass,
        };
}

internal static class SecurityAuditBatching
{
    public static IReadOnlyCollection<SecurityAuditEnvelope> ConsolidateAbuse(
        IReadOnlyCollection<SecurityAuditEnvelope> batch)
    {
        var result = batch
            .Where(x => x.DurabilityClass != AuditDurabilityClass.Abuse)
            .ToList();

        foreach (var group in batch
                     .Where(x => x.DurabilityClass == AuditDurabilityClass.Abuse)
                     .GroupBy(AbuseKey.From))
        {
            var first = group.First();
            var record = first.RealmRecord!;
            result.Add(SecurityAuditEnvelope.ForRealm(
                first.RealmSlug!,
                record with
                {
                    Count = group.Sum(x => x.RealmRecord!.Count ?? 1),
                    FirstObservedAt = group.Min(x => x.CapturedAt),
                    LastObservedAt = group.Max(x => x.CapturedAt),
                },
                AuditDurabilityClass.Abuse));
        }

        return result;
    }

    private sealed record AbuseKey(
        string RealmSlug,
        string EventType,
        string? ReasonCode,
        string? OperationCode,
        AuditActorKind? ActorKind,
        Guid? ActorSubjectId,
        Guid? TargetSubjectId,
        string? UnknownIdentifier,
        string? IpAddress,
        string? OAuthClientId,
        Guid? ApplicationId,
        Guid? LoginProviderId,
        string? AuthenticationMethod)
    {
        public static AbuseKey From(SecurityAuditEnvelope envelope)
        {
            var record = envelope.RealmRecord!;
            return new(
                envelope.RealmSlug!,
                record.EventType,
                record.ReasonCode,
                record.OperationCode,
                record.ActorKind,
                record.ActorSubjectId,
                record.TargetSubjectId,
                record.UnknownIdentifier,
                record.IpAddress,
                record.OAuthClientId,
                record.ApplicationId,
                record.LoginProviderId,
                record.AuthenticationMethod);
        }
    }
}

internal static class SecurityAuditPersistence
{
    private static readonly ConcurrentDictionary<string, byte[]> FingerprintKeys =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task PersistAsync(
        IReadOnlyCollection<SecurityAuditEnvelope> batch,
        IDocumentStore realmStore,
        IGlobalStore globalStore,
        CancellationToken ct)
    {
        foreach (var realmGroup in batch
                     .Where(x => x.RealmRecord is not null)
                     .GroupBy(x => x.RealmSlug!, StringComparer.OrdinalIgnoreCase))
        {
            var key = realmGroup.Any(x => x.RealmRecord!.UnknownIdentifier is not null)
                ? await GetOrCreateFingerprintKeyAsync(realmStore, realmGroup.Key, ct)
                : null;
            var events = realmGroup
                .Select(x => ToRealmEvent(x, key))
                .ToArray();

            await using var session = realmStore.LightweightSession(realmGroup.Key);
            session.Store(events);
            await session.SaveChangesAsync(ct);
        }

        var platformEvents = batch
            .Where(x => x.PlatformRecord is not null)
            .Select(ToPlatformEvent)
            .ToArray();

        if (platformEvents.Length > 0)
        {
            await using var session = globalStore.LightweightSession();
            session.Store(platformEvents);
            await session.SaveChangesAsync(ct);
        }
    }

    public static void StoreRequired(
        IDocumentSession session,
        SecurityAuditEnvelope envelope)
    {
        var record = envelope.RealmRecord
            ?? throw new ArgumentException("A realm audit envelope is required.", nameof(envelope));
        if (record.UnknownIdentifier is not null)
        {
            throw new InvalidOperationException(
                "Required events with an unknown identifier must use RecordRequiredAsync " +
                "so the identifier can be fingerprinted with the realm-owned key.");
        }

        if (!string.Equals(session.TenantId, envelope.RealmSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Audit realm '{envelope.RealmSlug}' does not match the Marten session tenant '{session.TenantId}'.");
        }

        session.Store(ToRealmEvent(envelope, key: null));
    }

    public static void StorePlatformRequired(
        IDocumentSession session,
        SecurityAuditEnvelope envelope)
    {
        if (envelope.PlatformRecord is null)
            throw new ArgumentException("A platform audit envelope is required.", nameof(envelope));

        session.Store(ToPlatformEvent(envelope));
    }

    private static RealmSecurityAuditEvent ToRealmEvent(
        SecurityAuditEnvelope envelope,
        byte[]? key)
    {
        var record = envelope.RealmRecord!;
        return new()
        {
            Id = envelope.Id,
            Timestamp = envelope.CapturedAt,
            Category = AuditEvents.CategoryOf(record.EventType),
            EventType = record.EventType,
            Severity = record.Severity,
            ActorKind = record.ActorKind ?? AuditActorKind.System,
            ActorSubjectId = record.ActorSubjectId,
            TargetSubjectId = record.TargetSubjectId,
            UnknownIdentifierFingerprint = record.UnknownIdentifier is null
                ? null
                : Fingerprint(
                    record.UnknownIdentifier,
                    key ?? throw new InvalidOperationException(
                        "An audit fingerprint key is required for an unknown identifier.")),
            IpAddress = record.IpAddress,
            UserAgent = record.UserAgent,
            OAuthClientId = record.OAuthClientId,
            AuthorizationId = record.AuthorizationId,
            ApplicationId = record.ApplicationId,
            SessionId = record.SessionId,
            LoginProviderId = record.LoginProviderId,
            AuthenticationMethod = record.AuthenticationMethod,
            CorrelationId = record.CorrelationId,
            OutcomeCode = record.OutcomeCode,
            ReasonCode = record.ReasonCode,
            OperationCode = record.OperationCode,
            TargetRealmSlug = record.TargetRealmSlug,
            KeyId = record.KeyId,
            Count = record.Count,
            RelatedCount = record.RelatedCount,
            RemindedCount = record.RemindedCount,
            SelfErasedCount = record.SelfErasedCount,
            AutoPurgedCount = record.AutoPurgedCount,
            InviteCodesPrunedCount = record.InviteCodesPrunedCount,
            ReusedCount = record.ReusedCount,
            RetentionDays = record.RetentionDays,
            EffectiveAt = record.EffectiveAt,
            FirstObservedAt = record.FirstObservedAt,
            LastObservedAt = record.LastObservedAt,
        };
    }

    private static PlatformAuditEvent ToPlatformEvent(SecurityAuditEnvelope envelope)
    {
        var record = envelope.PlatformRecord!;
        return new()
        {
            Id = envelope.Id,
            Timestamp = envelope.CapturedAt,
            Category = AuditEvents.CategoryOf(record.EventType),
            EventType = record.EventType,
            Severity = record.Severity,
            OutcomeCode = record.OutcomeCode,
            ReasonCode = record.ReasonCode,
            OperationCode = record.OperationCode,
            TargetRealmSlug = record.TargetRealmSlug,
            Domain = record.Domain,
            PreviousDomain = record.PreviousDomain,
            CorrelationId = record.CorrelationId,
            Count = record.Count,
            RelatedCount = record.RelatedCount,
            RetentionDays = record.RetentionDays,
            EffectiveAt = record.EffectiveAt,
        };
    }

    private static string Fingerprint(string identifier, byte[] key)
    {
        var normalized = identifier.Trim().Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static async Task<byte[]> GetOrCreateFingerprintKeyAsync(
        IDocumentStore store,
        string realmSlug,
        CancellationToken ct)
    {
        if (FingerprintKeys.TryGetValue(realmSlug, out var cached))
            return cached;

        await using (var read = store.QuerySession(realmSlug))
        {
            var existing = await read.LoadAsync<RealmAuditFingerprintKey>(
                RealmAuditFingerprintKey.SingletonId, ct);
            if (existing is not null)
                return FingerprintKeys.GetOrAdd(realmSlug, existing.Key);
        }

        var candidate = new RealmAuditFingerprintKey
        {
            Key = RandomNumberGenerator.GetBytes(32),
        };

        try
        {
            await using var write = store.LightweightSession(realmSlug);
            write.Insert(candidate);
            await write.SaveChangesAsync(ct);
            return FingerprintKeys.GetOrAdd(realmSlug, candidate.Key);
        }
        catch (Exception createError) when (createError is not OperationCanceledException)
        {
            // Another node may have created the singleton between our read and
            // insert. Use the winning key; if no row exists, preserve the real
            // storage failure instead of silently changing fingerprints.
            await using var retry = store.QuerySession(realmSlug);
            var winner = await retry.LoadAsync<RealmAuditFingerprintKey>(
                RealmAuditFingerprintKey.SingletonId, ct);
            if (winner is not null)
                return FingerprintKeys.GetOrAdd(realmSlug, winner.Key);

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(createError).Throw();
            throw;
        }
    }
}

public sealed class SecurityAuditWriter(
    IServiceProvider services,
    SecurityAuditLog log,
    ILogger<SecurityAuditWriter> logger) : BackgroundService
{
    private const int MaxBatch = 4_096;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = log.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<SecurityAuditEnvelope>(MaxBatch);
            while (batch.Count < MaxBatch && reader.TryRead(out var entry))
                batch.Add(entry);

            if (batch.Count == 0)
                continue;

            // Give attacker-amplified signals a short coalescing window. This
            // turns a credential-stuffing burst into a handful of count rows
            // instead of one database write per request.
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            while (batch.Count < MaxBatch && reader.TryRead(out var entry))
                batch.Add(entry);

            var consolidated = SecurityAuditBatching.ConsolidateAbuse(batch);
            var abuse = consolidated
                .Where(x => x.DurabilityClass == AuditDurabilityClass.Abuse)
                .ToArray();
            var telemetry = consolidated
                .Where(x => x.DurabilityClass == AuditDurabilityClass.Telemetry)
                .ToArray();

            if (abuse.Length > 0)
                await PersistAbuseWithRetryAsync(abuse, stoppingToken);

            if (telemetry.Length > 0)
            {
                try
                {
                    using var scope = services.CreateScope();
                    await SecurityAuditPersistence.PersistAsync(
                        telemetry,
                        scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
                        scope.ServiceProvider.GetRequiredService<IGlobalStore>(),
                        stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "Failed to persist {Count} best-effort audit telemetry record(s)",
                        telemetry.Length);
                }
            }

            var dropped = log.ReadAndResetDropped();
            if (dropped > 0)
            {
                logger.LogWarning(
                    "Security audit buffer shed {Dropped} abuse/telemetry occurrence(s) — channel full",
                    dropped);
            }
        }
    }

    private async Task PersistAbuseWithRetryAsync(
        IReadOnlyCollection<SecurityAuditEnvelope> batch,
        CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                await SecurityAuditPersistence.PersistAsync(
                    batch,
                    scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
                    scope.ServiceProvider.GetRequiredService<IGlobalStore>(),
                    ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Failed to persist {Count} aggregated abuse signal(s); retrying in {Delay}",
                    batch.Count,
                    delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
