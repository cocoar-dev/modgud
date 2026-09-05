using System.Reflection;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Cluster;
using Npgsql;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// Creates the Quartz.NET job-store tables in the master database once, before
/// the scheduler starts (ADR 0010, D4). Quartz validates its schema at start-up
/// and does not create it, so this runs in the same bootstrap step that applies
/// the Marten master/global schema. Idempotent and safe for two nodes booting at
/// the same time: the existence check and the script run under a cluster lock.
/// </summary>
public static class QuartzSchemaBootstrap
{
    public const string Schema = "quartz";
    private const string ProbeTable = "quartz.qrtz_job_details";
    private const string ResourceName = "Modgud.Infrastructure.Scheduling.quartz_tables_postgres.sql";

    public static async Task EnsureAsync(
        string masterConnectionString,
        IClusterLock clusterLock,
        ILogger logger,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(masterConnectionString);
        await connection.OpenAsync(ct);

        if (await ExistsAsync(connection, ct))
            return;

        await using var _ = await clusterLock.AcquireAsync(ClusterLockKeys.QuartzSchema, ct);

        // Re-check under the lock — the peer may have just finished the same script.
        if (await ExistsAsync(connection, ct))
            return;

        logger.LogInformation("[Jobs] Creating the Quartz job-store schema '{Schema}' in the master database", Schema);

        // The script is a compile-time embedded resource shipped with this
        // assembly — no user input reaches it (CA2100 is about that).
        var script = ReadScript();
        await using var transaction = await connection.BeginTransactionAsync(ct);
#pragma warning disable CA2100
        await using (var cmd = new NpgsqlCommand(script, connection, transaction))
#pragma warning restore CA2100
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        cmd.Parameters.AddWithValue("name", ProbeTable);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
