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
            await using var cleanup = new NpgsqlConnection(builder.ConnectionString);
            await cleanup.OpenAsync(ct);
#pragma warning disable CA2100 // name is a generated modgud_review_ identifier
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", cleanup);
#pragma warning restore CA2100
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}
