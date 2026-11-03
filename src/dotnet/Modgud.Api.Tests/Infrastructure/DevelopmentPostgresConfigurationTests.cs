namespace Modgud.Api.Tests.Infrastructure;

public class DevelopmentPostgresConfigurationTests
{
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Username=modgud_dev;Database=modgud_review_unique")]
    [InlineData("Host=postgres;Port=5433;Username=modgud_dev;Database=modgud_review_unique")]
    [InlineData("Host=127.0.0.1;Port=5433;Username=postgres;Database=modgud_review_unique")]
    [InlineData("Host=127.0.0.1;Port=5433;Username=modgud_dev;Database=modgud_dev")]
    [InlineData("Host=127.0.0.1;Port=5433;Username=modgud_dev;Database=modgud_review_a-b")]
    public void Rejects_nonisolated_targets(string connectionString) =>
        Assert.Throws<InvalidOperationException>(() =>
            TestPostgres.ValidateDevelopmentConnectionString(connectionString));

    [Fact]
    public void Accepts_only_explicit_development_target()
    {
        var validated = TestPostgres.ValidateDevelopmentConnectionString(
            "Host=127.0.0.1;Port=5433;Username=modgud_dev;Database=modgud_review_unique");
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(validated);
        Assert.Equal(5433, builder.Port);
        Assert.Equal(60, builder.Timeout);
    }

    [Fact]
    public async Task Refuses_an_existing_derived_realm_before_creating_the_master()
    {
        var supplied = Environment.GetEnvironmentVariable("MODGUD_TEST_POSTGRES_CONNECTION_STRING");
        Assert.SkipWhen(supplied is null, "Requires the explicit postgres-dev opt-in.");
        var target = new Npgsql.NpgsqlConnectionStringBuilder(TestPostgres.ValidateDevelopmentConnectionString(supplied!))
        {
            Database = "modgud_review_guard_" + Guid.NewGuid().ToString("N")[..12],
        };
        var master = target.Database!;
        var realm = master + "_system";
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
        await using var connection = new Npgsql.NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var quoted = new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(realm);
#pragma warning disable CA2100
        await using (var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {quoted}", connection))
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
#pragma warning restore CA2100
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TestPostgres.CreateDevelopmentDatabaseAsync(target.ConnectionString));
            await using var check = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @master", connection);
            check.Parameters.AddWithValue("master", master);
            Assert.Equal(0L, await check.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            // Only this test's exclusively created empty orphan is removed.
#pragma warning disable CA2100
            await using var drop = new Npgsql.NpgsqlCommand($"DROP DATABASE {quoted}", connection);
#pragma warning restore CA2100
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}
