using Npgsql;

namespace Modgud.Api.Startup;

/// <summary>
/// Creates the master database when it does not exist yet.
/// <para>B9-ST-01 — the existence check and CREATE DATABASE are not atomic: two instances
/// starting together against a missing database both see "absent", one creates it and the
/// other failed with duplicate_database (42P04), which ended its boot. Losing that race is
/// success: the database exists.</para>
/// </summary>
public static class MasterDatabaseBootstrap
{
    /// <returns><c>true</c> when this call created the database.</returns>
    public static async Task<bool> EnsureExistsAsync(NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using var checkCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @dbName", connection);
        checkCmd.Parameters.AddWithValue("@dbName", databaseName);
        if (await checkCmd.ExecuteScalarAsync(ct) is not null) return false;

        var quotedName = "\"" + databaseName.Replace("\"", "\"\"") + "\"";
#pragma warning disable CA2100
        await using var createCmd = new NpgsqlCommand($"CREATE DATABASE {quotedName}", connection);
#pragma warning restore CA2100
        try
        {
            await createCmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateDatabase or PostgresErrorCodes.UniqueViolation)
        {
            // Another instance created it between our check and our CREATE.
            return false;
        }
    }
}
