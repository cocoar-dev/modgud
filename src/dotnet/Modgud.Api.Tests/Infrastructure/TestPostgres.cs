using Npgsql;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>Connection-string adjustments shared by the Testcontainers fixtures.</summary>
public static class TestPostgres
{
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
