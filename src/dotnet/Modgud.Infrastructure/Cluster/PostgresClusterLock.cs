using Modgud.Infrastructure.Persistence.Tenancy;
using Npgsql;

namespace Modgud.Infrastructure.Cluster;

/// <summary>
/// A deployment-wide mutual exclusion primitive on the master database: a
/// session-scoped Postgres advisory lock held on a dedicated connection for the
/// lifetime of the returned handle. Used for the few maintenance steps that
/// two booting nodes must not run at the same time (Quartz schema bootstrap,
/// schedule reconciliation). Postgres releases the lock the moment the holding
/// connection dies, so a crashed node can never leave it stuck.
/// </summary>
public interface IClusterLock
{
    /// <summary>
    /// Blocks until the lock named by <paramref name="key"/> is held and returns a
    /// handle that releases it. Keys are stable per purpose — see <see cref="ClusterLockKeys"/>.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken ct = default);
}

public static class ClusterLockKeys
{
    // Arbitrary but fixed 64-bit keys; only uniqueness among our own uses matters.
    public const long QuartzSchema = 0x4D47_5152_545A_0001;   // "MGQRTZ" 1
    public const long QuartzSchedules = 0x4D47_5152_545A_0002; // "MGQRTZ" 2
}

internal sealed class PostgresClusterLock(IMasterConnectionString master) : IClusterLock
{
    public async Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(master.Value);
        try
        {
            await connection.OpenAsync(ct);
            await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
            {
                cmd.Parameters.AddWithValue("key", key);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return new Handle(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Handle(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                cmd.Parameters.AddWithValue("key", key);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Closing the connection releases a session-scoped advisory lock anyway.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>Single-process hosts (Development, Testing) do not need cross-node exclusion.</summary>
internal sealed class NoClusterLock : IClusterLock
{
    public Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable>(new Nothing());

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
