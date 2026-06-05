using Npgsql;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// Applies the Marten schema to a freshly-registered tenant database resiliently.
///
/// <para>When a new tenant database is registered, Marten's async projection daemon
/// (running in <c>DaemonMode.Solo</c>) auto-discovers it on its next health check and
/// concurrently ensures the event/projection schema — racing the eager
/// <c>ApplyAllConfiguredChangesToDatabaseAsync</c> in <c>RealmProvisioningService</c>.
/// Both appliers can try to create the same objects at once (e.g. <c>mt_events</c> and
/// its <c>fkey_mt_events_stream_id</c>), so the loser hits an "already exists" /
/// lock-contention error.</para>
///
/// <para>Marten exposes no first-class "provision a tenant offline first" path
/// (<c>AddDatabaseRecordAsync</c> has no <c>disabled</c> flag, and disabled tenants are
/// filtered out of tenant resolution so they cannot have schema applied), so we tolerate
/// the benign collision: the apply is idempotent (<c>CreateOrUpdate</c>), so re-applying
/// after the racer settles converges. See JasperFx/marten discussion #3104.</para>
/// </summary>
public static class TenantSchemaProvisioning
{
    /// <summary>
    /// Postgres SQLSTATEs that signal a benign concurrent-DDL collision (an object was
    /// created by a racing applier) or transient lock contention during schema
    /// application — safe to retry because the apply is idempotent.
    /// </summary>
    public static readonly IReadOnlySet<string> ConcurrentSchemaSqlStates = new HashSet<string>
    {
        PostgresErrorCodes.DuplicateObject,    // 42710 — e.g. duplicate constraint (observed)
        PostgresErrorCodes.DuplicateTable,     // 42P07
        PostgresErrorCodes.DuplicateColumn,    // 42701
        PostgresErrorCodes.DuplicateFunction,  // 42723
        PostgresErrorCodes.DuplicateSchema,    // 42P06
        PostgresErrorCodes.UniqueViolation,    // 23505 — concurrent insert into Marten metadata
        PostgresErrorCodes.LockNotAvailable,   // 55P03
        PostgresErrorCodes.DeadlockDetected,   // 40P01
    };

    /// <summary>
    /// Walks the inner-exception chain (Marten wraps the driver error in a
    /// <c>MartenSchemaException</c>) looking for a concurrent-schema-conflict SQLSTATE.
    /// </summary>
    public static bool TryFindConcurrentSchemaConflict(Exception exception, out PostgresException? conflict)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg && pg.SqlState is not null
                && ConcurrentSchemaSqlStates.Contains(pg.SqlState))
            {
                conflict = pg;
                return true;
            }
        }

        conflict = null;
        return false;
    }

    /// <summary>
    /// Runs <paramref name="apply"/>, retrying on a concurrent-schema conflict using the
    /// supplied <paramref name="backoff"/>. Rethrows after <paramref name="maxAttempts"/>
    /// attempts, or immediately on any non-conflict exception. <paramref name="onRetry"/>
    /// is invoked (with the conflict and the 1-based attempt number) before each wait.
    /// </summary>
    public static async Task ApplyWithRetryAsync(
        Func<Task> apply,
        int maxAttempts,
        Func<int, TimeSpan> backoff,
        Action<PostgresException, int>? onRetry,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await apply();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && TryFindConcurrentSchemaConflict(ex, out var conflict))
            {
                onRetry?.Invoke(conflict!, attempt);
                await Task.Delay(backoff(attempt), ct);
            }
        }
    }
}
