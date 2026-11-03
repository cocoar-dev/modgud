using Npgsql;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>Connection-string adjustments shared by the Testcontainers fixtures.</summary>
public static class TestPostgres
{
    /// <summary>
    /// Explicit opt-in for local integration runs against postgres-dev. Never
    /// accept the stable port or an existing application database: the fixture
    /// creates a fresh, exclusively owned database before starting its host.
    /// </summary>
    public static string ValidateDevelopmentConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Host != "127.0.0.1" || builder.Port != 5433 ||
            builder.Username != "modgud_dev" ||
            builder.Database is not { Length: > 14 and <= 40 } database ||
            !database.StartsWith("modgud_review_", StringComparison.Ordinal) ||
            database.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new InvalidOperationException(
                "Local integration tests require 127.0.0.1:5433, user modgud_dev, " +
                "and a new modgud_review_* database (maximum 40 identifier characters).");
        }

        return WithGenerousConnectTimeout(builder.ConnectionString);
    }

    public static async Task<string> CreateDevelopmentDatabaseAsync(string connectionString)
    {
        var validated = ValidateDevelopmentConnectionString(connectionString);
        var admin = new NpgsqlConnectionStringBuilder(validated);
        var database = admin.Database!;
        admin.Database = "postgres";
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        // Marten derives realm database names from the master name. An orphaned
        // *_system database must not be silently reused by the test data reset.
        await using var existing = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name OR starts_with(datname, @prefix))", connection);
        existing.Parameters.AddWithValue("name", database);
        existing.Parameters.AddWithValue("prefix", database + "_");
        if (await existing.ExecuteScalarAsync() is true)
            throw new InvalidOperationException("The development test database or a derived realm database already exists; choose a new unique name.");

        // Exclusively create both databases before the factory can reset data.
        // No IF NOT EXISTS: even a concurrent creator of *_system must fail,
        // rather than being reused by ProvisionLegacyTestRealmAsync.
        foreach (var name in new[] { database, database + "_system" })
        {
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
#pragma warning disable CA2100
            await using var create = new NpgsqlCommand($"CREATE DATABASE {quoted}", connection);
#pragma warning restore CA2100
            await create.ExecuteNonQueryAsync();
        }
        Console.WriteLine($"Isolated integration target verified: 127.0.0.1:5433/{database} (fresh database).");
        return validated;
    }

    /// <summary>
    /// Raises Npgsql's connect timeout from its 15 s default. The suites boot a host per
    /// test, and each boot opens fresh connections through Docker Desktop's published-port
    /// proxy while other containers may be starting or stopping. A TCP connect that stalls
    /// past 15 s surfaces as "Timeout during connection attempt" at
    /// <c>ApplyAllConfiguredChangesToDatabaseAsync</c> and fails an unrelated test once in
    /// a long run. Realm databases derive their connection strings from this one, so the
    /// setting reaches them too. The value also bounds the wait for a pooled connection.
    /// </summary>
    public static string WithGenerousConnectTimeout(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 60 }.ConnectionString;
}
