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
/// Best-effort in-process buffer for realm-owned security events and PII-free
/// platform events. The background writer routes each envelope to its owning
/// physical store. F7 (durable delivery) remains a separate decision.
/// </summary>
public sealed class SecurityAuditLog(IHttpContextAccessor httpContextAccessor) : ISecurityAuditLog
{
    private readonly Channel<SecurityAuditEnvelope> _channel =
        Channel.CreateBounded<SecurityAuditEnvelope>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private long _dropped;

    internal ChannelReader<SecurityAuditEnvelope> Reader => _channel.Reader;
    internal long ReadAndResetDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Record(SecurityAuditRecord record)
    {
        try
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
            var correlationId = record.CorrelationId ?? CurrentCorrelationId(http);
            var actorKind = record.ActorKind
                ?? (subject is not null
                    ? AuditActorKind.User
                    : record.UnknownIdentifier is not null
                        ? AuditActorKind.AnonymousIdentifier
                        : record.OAuthClientId is not null
                            ? AuditActorKind.OAuthClient
                            : AuditActorKind.System);

            Enqueue(SecurityAuditEnvelope.ForRealm(
                record.RealmSlug ?? TenantContext.Current,
                record with
                {
                    ActorSubjectId = subject,
                    IpAddress = ip,
                    UserAgent = userAgent,
                    CorrelationId = correlationId,
                    ActorKind = actorKind,
                }));
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void RecordPlatform(PlatformAuditRecord record)
    {
        try
        {
            Enqueue(SecurityAuditEnvelope.ForPlatform(record with
            {
                CorrelationId = record.CorrelationId ?? CurrentCorrelationId(httpContextAccessor.HttpContext),
            }));
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
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
            await SecurityAuditPersistence.PersistAsync(batch, realmStore, globalStore, ct);
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
    public required string? RealmSlug { get; init; }
    public SecurityAuditRecord? RealmRecord { get; init; }
    public PlatformAuditRecord? PlatformRecord { get; init; }

    public static SecurityAuditEnvelope ForRealm(string realmSlug, SecurityAuditRecord record)
        => new() { RealmSlug = realmSlug, RealmRecord = record };

    public static SecurityAuditEnvelope ForPlatform(PlatformAuditRecord record)
        => new() { RealmSlug = null, PlatformRecord = record };
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
            var key = await GetOrCreateFingerprintKeyAsync(realmStore, realmGroup.Key, ct);
            var events = realmGroup
                .Select(x => ToRealmEvent(x.RealmRecord!, key))
                .ToArray();

            await using var session = realmStore.LightweightSession(realmGroup.Key);
            session.Store(events);
            await session.SaveChangesAsync(ct);
        }

        var platformEvents = batch
            .Where(x => x.PlatformRecord is not null)
            .Select(x => ToPlatformEvent(x.PlatformRecord!))
            .ToArray();

        if (platformEvents.Length > 0)
        {
            await using var session = globalStore.LightweightSession();
            session.Store(platformEvents);
            await session.SaveChangesAsync(ct);
        }
    }

    private static RealmSecurityAuditEvent ToRealmEvent(SecurityAuditRecord record, byte[] key)
        => new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = AuditEvents.CategoryOf(record.EventType),
            EventType = record.EventType,
            Severity = record.Severity,
            ActorKind = record.ActorKind ?? AuditActorKind.System,
            ActorSubjectId = record.ActorSubjectId,
            TargetSubjectId = record.TargetSubjectId,
            UnknownIdentifierFingerprint = record.UnknownIdentifier is null
                ? null
                : Fingerprint(record.UnknownIdentifier, key),
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
        };

    private static PlatformAuditEvent ToPlatformEvent(PlatformAuditRecord record)
        => new()
        {
            Timestamp = DateTimeOffset.UtcNow,
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
    private const int MaxBatch = 256;

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

            try
            {
                using var scope = services.CreateScope();
                await SecurityAuditPersistence.PersistAsync(
                    batch,
                    scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
                    scope.ServiceProvider.GetRequiredService<IGlobalStore>(),
                    stoppingToken);
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
