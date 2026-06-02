using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Migrations;
using Modgud.Infrastructure.Persistence.Tenancy;
using Npgsql;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// WS2 of the Account-Lifecycle plan: the self-removing per-realm migration
/// that establishes the email-uniqueness invariant. Exercises the three
/// behaviours against the system tenant DB (its own <c>{master}_system</c> DB
/// since the master/system split):
/// builds the partial unique index + enforces it, scrubs legacy deleted-user
/// PII, and refuses to build the index when active duplicates exist.
///
/// <para>Tests manipulate <c>public.mt_doc_applicationuser</c> directly (raw
/// SQL) so they can create the duplicate / deleted rows that the app's
/// write-path guards would otherwise refuse. Each test rebuilds a clean index
/// in its <c>finally</c> so the sequential run leaves a consistent backstop.</para>
/// </summary>
public class EmailUniquenessMigrationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Table = "public.mt_doc_applicationuser";
    private const string DropIndexSql =
        $"DROP INDEX IF EXISTS public.{EmailUniquenessMigration.IndexName}";
    private const string IndexExistsSql =
        $"SELECT to_regclass('public.{EmailUniquenessMigration.IndexName}')::text";

    [Fact]
    public async Task Migration_builds_partial_unique_index_and_db_rejects_active_duplicate()
    {
        await using var conn = await OpenSystemDbAsync();
        try
        {
            await ExecAsync(conn, DropIndexSql);
            await RunMigrationAsync();

            Assert.True(await IndexExistsAsync(conn), "Migration should have built the unique index on a clean realm.");

            // First active user with this email — fine.
            await InsertRawUserAsync(conn, "uniq-a@test.com", isDeleted: false);

            // Second active user with the SAME normalized email — the partial
            // unique index must reject it at the DB level (the backstop).
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRawUserAsync(conn, "UNIQ-A@test.com", isDeleted: false));
            Assert.Equal("23505", ex.SqlState); // unique_violation
        }
        finally
        {
            await RestoreCleanIndexAsync(conn);
        }
    }

    [Fact]
    public async Task Migration_nulls_email_on_already_deleted_users_but_keeps_active_emails()
    {
        await using var conn = await OpenSystemDbAsync();
        try
        {
            await ExecAsync(conn, DropIndexSql);

            var ghostId = await InsertRawUserAsync(conn, "ghost@test.com", isDeleted: true);
            var liveId = await InsertRawUserAsync(conn, "live@test.com", isDeleted: false);

            await RunMigrationAsync();

            Assert.Null(await ReadEmailAsync(conn, ghostId, "NormalizedEmail"));
            Assert.Null(await ReadEmailAsync(conn, ghostId, "Email"));
            Assert.Equal("LIVE@TEST.COM", await ReadEmailAsync(conn, liveId, "NormalizedEmail"));
            Assert.True(await IndexExistsAsync(conn), "With no active duplicates the index should be built.");
        }
        finally
        {
            await RestoreCleanIndexAsync(conn);
        }
    }

    [Fact]
    public async Task Migration_refuses_to_build_index_while_active_duplicates_exist()
    {
        await using var conn = await OpenSystemDbAsync();
        try
        {
            await ExecAsync(conn, DropIndexSql);

            // Two ACTIVE users sharing a normalized email — only possible with
            // the index absent (the app's write guards would refuse them).
            await InsertRawUserAsync(conn, "twin@test.com", isDeleted: false);
            await InsertRawUserAsync(conn, "TWIN@test.com", isDeleted: false);

            await RunMigrationAsync();

            Assert.False(await IndexExistsAsync(conn),
                "Migration must REFUSE to build the unique index while active duplicates exist.");
        }
        finally
        {
            // Remove the duplicates so the clean rebuild can succeed.
            await ExecAsync(conn,
                $"DELETE FROM {Table} WHERE upper(data ->> 'Email') = 'TWIN@TEST.COM'");
            await RestoreCleanIndexAsync(conn);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> OpenSystemDbAsync()
    {
        var masterCs = Factory.Services.GetRequiredService<IMasterConnectionString>().Value;
        // Since the master/system DB split the system tenant lives in its own
        // {master}_system database — the migration runs there, not the master DB.
        var builder = new NpgsqlConnectionStringBuilder(masterCs);
        builder.Database = $"{builder.Database}_{TenantConstants.SystemTenantId}";
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private async Task RunMigrationAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<EmailUniquenessMigration>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes any rows this test inserted (matched by the test-only
    /// email domain) and re-runs the migration so a valid index is back in
    /// place for the next sequential test.</summary>
    private async Task RestoreCleanIndexAsync(NpgsqlConnection conn)
    {
        await ExecAsync(conn,
            $"DELETE FROM {Table} WHERE data ->> 'Email' ~* '@test\\.com$' AND data ->> 'Email' <> 'test@test.com'");
        await RunMigrationAsync();
    }

    private async Task<Guid> InsertRawUserAsync(NpgsqlConnection conn, string email, bool isDeleted)
    {
        var id = Guid.NewGuid();
        var data = $$"""
            {"Id":"{{id}}","UserName":"{{id:N}}","NormalizedUserName":"{{id:N}}",
             "Email":"{{email}}","NormalizedEmail":"{{email.ToUpperInvariant()}}",
             "IsActive":true,"IsDeleted":{{(isDeleted ? "true" : "false")}}}
            """;
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {Table} (id, data, mt_last_modified, mt_version) " +
            "VALUES (@id, @data::jsonb, now(), gen_random_uuid())", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("data", data);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<string?> ReadEmailAsync(NpgsqlConnection conn, Guid id, string field)
    {
        // CA2100: field/Table are test-only constants, not user input.
#pragma warning disable CA2100
        await using var cmd = new NpgsqlCommand(
            $"SELECT data ->> '{field}' FROM {Table} WHERE id = @id", conn);
#pragma warning restore CA2100
        cmd.Parameters.AddWithValue("id", id);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? null : (string)result;
    }

    private async Task<bool> IndexExistsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(IndexExistsSql, conn);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is not (null or DBNull);
    }

    private async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        // CA2100: callers pass only test-controlled constant SQL.
#pragma warning disable CA2100
        await using var cmd = new NpgsqlCommand(sql, conn);
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
