using Modgud.Api.Startup;
using Modgud.Api.Tests.Infrastructure;
using Npgsql;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B9-ST-01 — two instances starting together against a missing master database both saw it
/// absent; one created it and the other failed with duplicate_database, ending its boot.
/// Concurrent first starts must all succeed. Uses a fresh modgud_review_* database on the
/// verified development server and drops it again.
/// </summary>
public sealed class MasterDatabaseBootstrapRaceTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private readonly SharedPostgresFixture _fixture = fixture;

    /// <summary>DROP DATABASE waits for a checkpoint; on a busy server (the full suite creates
    /// and drops realm databases meanwhile) that exceeded Npgsql's 30 s default.</summary>
    private const int DropTimeoutSeconds = 300;

    [Fact]
    public async Task Concurrent_first_starts_all_succeed_and_one_creates_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres", Pooling = false };
        var name = $"modgud_review_st01{Guid.NewGuid():N}"[..40];
        try
        {
            var attempts = Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(ct);
                return await MasterDatabaseBootstrap.EnsureExistsAsync(connection, name, ct);
            }).ToList();

            var created = await Task.WhenAll(attempts);
            Assert.Equal(1, created.Count(c => c));
        }
        finally
        {
            // Never let the cleanup replace the outcome: an exception thrown here would hide
            // the assertion above (or turn a passed race into a failure).
            await DropAsync(builder.ConnectionString, name);
        }
    }

    private static async Task DropAsync(string connectionString, string name)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var cleanup = new NpgsqlConnection(connectionString);
                await cleanup.OpenAsync(CancellationToken.None);
#pragma warning disable CA2100 // name is a generated modgud_review_ identifier
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", cleanup)
                {
                    CommandTimeout = DropTimeoutSeconds,
                };
#pragma warning restore CA2100
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // Reported, not thrown: the database is work-owned (modgud_review_*) and is
                // removed with the other review databases.
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"MODGUD_TEST_CLEANUP attempt {attempt}/2: dropping {name} failed: {ex.GetType().Name}");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"MODGUD_TEST_CLEANUP_LEFT {name}");
    }
}
