using System.Collections.Concurrent;
using Modgud.Domain.Realms;
using Npgsql;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>
/// ADR 0019 — counters in Postgres, one atomic upsert per hit, so N Modgud instances
/// agree on every count without new infrastructure. Realm policies live in the realm's
/// tenant database, realm-independent ones in the global store.
///
/// <para>The table is created lazily per database (<c>CREATE TABLE IF NOT EXISTS</c>,
/// cached per database after the first success). Rows are keyed by policy, dimension
/// and bucket value; <see cref="PruneAsync"/> drops rows untouched for a while (the
/// hourly hygiene job calls it).</para>
/// </summary>
public sealed class PostgresRateLimitStore(IRateLimitConnectionSource connections) : IRateLimitStore
{
    public const string TableName = "modgud_auth_rate_limit";

    private static readonly ConcurrentDictionary<string, bool> Ensured = new(StringComparer.Ordinal);

    private const string Ddl = $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            key text PRIMARY KEY,
            window_start timestamptz NULL,
            hits integer NOT NULL DEFAULT 0,
            tokens double precision NULL,
            denied boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS {TableName}_updated_at_idx ON {TableName} (updated_at);
        """;

    // Fixed window: the row carries the window it was last counted in; a new window resets.
    private const string FixedWindowSql = $"""
        INSERT INTO {TableName} AS t (key, window_start, hits, tokens, denied, updated_at)
        VALUES (@key, @ws, 1, NULL, false, @now)
        ON CONFLICT (key) DO UPDATE SET
            hits = CASE WHEN t.window_start = EXCLUDED.window_start THEN t.hits + 1 ELSE 1 END,
            window_start = EXCLUDED.window_start,
            tokens = NULL,
            denied = false,
            updated_at = EXCLUDED.updated_at
        RETURNING hits;
        """;

    // Token bucket: refill since the last hit, then take one token if a whole one is
    // there. The refilled value is computed from the LOCKED row (t.*), so concurrent
    // hits serialise correctly; "denied" records whether this hit consumed nothing.
    private const string TokenBucketSql = $"""
        INSERT INTO {TableName} AS t (key, window_start, hits, tokens, denied, updated_at)
        VALUES (@key, NULL, 0, @cap - 1, false, @now)
        ON CONFLICT (key) DO UPDATE SET
            tokens = CASE
                WHEN LEAST(@cap, COALESCE(t.tokens, @cap) + GREATEST(0, EXTRACT(EPOCH FROM (EXCLUDED.updated_at - t.updated_at))) * @rate) >= 1
                THEN LEAST(@cap, COALESCE(t.tokens, @cap) + GREATEST(0, EXTRACT(EPOCH FROM (EXCLUDED.updated_at - t.updated_at))) * @rate) - 1
                ELSE LEAST(@cap, COALESCE(t.tokens, @cap) + GREATEST(0, EXTRACT(EPOCH FROM (EXCLUDED.updated_at - t.updated_at))) * @rate)
            END,
            denied = LEAST(@cap, COALESCE(t.tokens, @cap) + GREATEST(0, EXTRACT(EPOCH FROM (EXCLUDED.updated_at - t.updated_at))) * @rate) < 1,
            window_start = NULL,
            updated_at = EXCLUDED.updated_at
        RETURNING tokens, denied;
        """;

    public async Task<RateLimitHit> HitAsync(RateLimitScope scope, string key, RateLimitRule rule, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(scope, ct);
        await EnsureTableAsync(connection, ct);

        if (rule.IsTokenBucket)
        {
            var (capacity, rate) = RateLimitMath.Bucket(rule);
            await using var cmd = new NpgsqlCommand(TokenBucketSql, connection);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("cap", capacity);
            cmd.Parameters.AddWithValue("rate", rate);
            cmd.Parameters.AddWithValue("now", now.UtcDateTime);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var tokens = reader.GetDouble(0);
            var denied = reader.GetBoolean(1);
            return denied
                ? new RateLimitHit(false, RateLimitMath.RetryAfterForBucket(tokens, rule))
                : new RateLimitHit(true, 0);
        }
        else
        {
            await using var cmd = new NpgsqlCommand(FixedWindowSql, connection);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("ws", RateLimitMath.WindowStart(now, rule).UtcDateTime);
            cmd.Parameters.AddWithValue("now", now.UtcDateTime);
            var hits = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return hits <= Math.Max(1, rule.PermitLimit)
                ? new RateLimitHit(true, 0)
                : new RateLimitHit(false, RateLimitMath.RetryAfterForWindow(now, rule));
        }
    }

    public async Task<RateLimitHit> PeekAsync(RateLimitScope scope, string key, RateLimitRule rule, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(scope, ct);
        await EnsureTableAsync(connection, ct);
        await using var cmd = new NpgsqlCommand($"SELECT window_start, hits, tokens, updated_at FROM {TableName} WHERE key = @key", connection);
        cmd.Parameters.AddWithValue("key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new RateLimitHit(true, 0);

        if (rule.IsTokenBucket)
        {
            var (capacity, rate) = RateLimitMath.Bucket(rule);
            var tokens = reader.IsDBNull(2) ? capacity : reader.GetDouble(2);
            var updatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
            var refilled = Math.Min(capacity, tokens + Math.Max(0, (now - updatedAt).TotalSeconds) * rate);
            return refilled >= 1 ? new RateLimitHit(true, 0) : new RateLimitHit(false, RateLimitMath.RetryAfterForBucket(refilled, rule));
        }

        if (reader.IsDBNull(0)) return new RateLimitHit(true, 0);
        var windowStart = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
        if (windowStart != RateLimitMath.WindowStart(now, rule)) return new RateLimitHit(true, 0);
        var hits = reader.GetInt32(1);
        return hits < Math.Max(1, rule.PermitLimit)
            ? new RateLimitHit(true, 0)
            : new RateLimitHit(false, RateLimitMath.RetryAfterForWindow(now, rule));
    }

    public async Task<int> PruneAsync(RateLimitScope scope, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(scope, ct);
        await EnsureTableAsync(connection, ct);
        await using var cmd = new NpgsqlCommand($"DELETE FROM {TableName} WHERE updated_at < @cutoff", connection);
        cmd.Parameters.AddWithValue("cutoff", olderThan.UtcDateTime);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var dbKey = $"{connection.Host}:{connection.Port}/{connection.Database}";
        if (Ensured.ContainsKey(dbKey)) return;
        try
        {
            await using var cmd = new NpgsqlCommand(Ddl, connection);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "42P07" or "42710")
        {
            // Two instances created the table at the same moment; it exists now.
        }
        Ensured[dbKey] = true;
    }
}
