using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.RateLimiting;
using Modgud.Infrastructure.Realms;
using Npgsql;

namespace Modgud.Api.Tests.Authorization;

// One bounded probe: two phases, two pairs each, at most eight HTTP requests.
// PostgreSQL itself must witness both real store commands waiting at our lock.
public sealed class RateLimitSqlOverlapTests(SharedPostgresFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Cold_initialization_and_post_reset_upserts_overlap_in_Postgres()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Factory.Services.GetRequiredService<IRateLimitConnectionSource>();
        var configured = Environment.GetEnvironmentVariable(TestFailureCorrelation.DirectoryVariable);
        var directory = configured ?? Directory.CreateTempSubdirectory("modgud-sql-overlap-").FullName;
        var completed = false;
        try
        {
            // Do not rely on the shared fixture's test order: its system/global
            // counters may already be warm. Provision our own inactive realm.
            var slug = $"sql-{Guid.NewGuid():N}"[..16];
            string database;
            await using (var connection = await source.OpenAsync(RateLimitScope.Global, ct))
            {
                if (Environment.GetEnvironmentVariable("MODGUD_TEST_POSTGRES_CONNECTION_STRING") is { } development)
                {
                    var expected = new NpgsqlConnectionStringBuilder(
                        TestPostgres.ValidateDevelopmentConnectionString(development));
                    Assert.True(connection.Host == expected.Host && connection.Port == expected.Port &&
                                connection.Database == expected.Database);
                }
                database = connection.Database + "_" + slug;
                await using var fresh = new NpgsqlCommand(
                    "SELECT NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", connection);
                fresh.Parameters.AddWithValue("name", database);
                Assert.True(await fresh.ExecuteScalarAsync(ct) is true);
            }
            var realm = await Factory.Services.GetRequiredService<IRealmProvisioningService>()
                .CreateRealmAsync(new CreateRealmDto
                {
                    Slug = slug, DisplayName = "SQL overlap probe",
                    Domains = [$"{slug}.localhost"], IsActive = false,
                }, ct);
            Assert.False(realm.IsError);
            var scope = new RateLimitScope(slug);
            await using (var connection = await source.OpenAsync(scope, ct))
            {
                Assert.True(connection.Database == database);
                await using var absent = new NpgsqlCommand(
                    "SELECT to_regclass('public.modgud_auth_rate_limit') IS NULL", connection);
                Assert.True(await absent.ExecuteScalarAsync(ct) is true,
                    "Cold probe requires a fresh counter table; never drop or reset an existing one.");
            }

            await RunPairAsync(source, scope, directory, "cold-bucket", RateLimitRule.Bucket(1, 60, 1), cold: true);
            await RunPairAsync(source, scope, directory, "cold-fixed", RateLimitRule.Fixed(1, 60), cold: false);
            await Factory.ResetMartenDataAsync();
            await RunPairAsync(source, scope, directory, "reset-bucket", RateLimitRule.Bucket(1, 60, 1), cold: false);
            await RunPairAsync(source, scope, directory, "reset-fixed", RateLimitRule.Fixed(1, 60), cold: false);
            completed = true;
        }
        finally
        {
            // Keep opt-in evidence and all failure evidence. Only clean up a
            // successful temporary directory exclusively created by this test.
            if (completed && configured is null) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunPairAsync(
        IRateLimitConnectionSource source, RateLimitScope scope, string directory, string phase, RateLimitRule rule, bool cold)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var observed = new ObservedConnections(source);
        var store = new PostgresRateLimitStore(observed);
        var correlation = new TestFailureCorrelation(directory);
        var key = $"overlap|{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        using var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IStartupFilter>(correlation);
            })
            .Configure(app =>
            {
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = context => context.Response.WriteAsync("probe-failed"),
                });
                app.Run(async context =>
                {
                    var hit = await store.HitAsync(scope, key, rule, now, context.RequestAborted);
                    context.Response.StatusCode = hit.Allowed ? 200 : 429;
                });
            })).Start();
        using var client = host.GetTestClient();
        correlation.AttachClient(client, TestContext.Current.TestOutputHelper);
        Task<HttpResponseMessage>[] requests = [];
        try
        {
            await using var blocker = await source.OpenAsync(scope, ct);
            await using var monitor = await source.OpenAsync(scope, ct);
            // Dispose/rollback this transaction before draining requests on any
            // assertion failure. Never leave a failed probe holding its SQL lock.
            await using (var transaction = await blocker.BeginTransactionAsync(ct))
            {
                var sql = cold ? """
                    CREATE TABLE public.modgud_auth_rate_limit (
                        key text PRIMARY KEY,
                        window_start timestamptz NULL,
                        hits integer NOT NULL DEFAULT 0,
                        tokens double precision NULL,
                        denied boolean NOT NULL DEFAULT false,
                        updated_at timestamptz NOT NULL
                    );
                    CREATE INDEX modgud_auth_rate_limit_updated_at_idx
                        ON public.modgud_auth_rate_limit (updated_at);
                    """ : "LOCK TABLE public.modgud_auth_rate_limit IN ACCESS EXCLUSIVE MODE";
                await using (var command = new NpgsqlCommand(sql, blocker, transaction))
                    await command.ExecuteNonQueryAsync(ct);

                requests = [client.GetAsync("/", ct), client.GetAsync("/", ct)];
                // A 500 before both connections open must not be hidden by a
                // barrier timeout. An already-running second request is drained.
                await Task.WhenAny(observed.PairOpened.Task, Task.WhenAny(requests)).WaitAsync(ct);
                StopOn500(requests);
                var pids = observed.Pids;
                Assert.Equal(2, pids.Length);
                Assert.Equal(2, pids.Distinct().Count());
                await AwaitSqlOverlapAsync(monitor, pids, blocker.ProcessID,
                    cold ? "CREATE TABLE IF NOT EXISTS %" : "INSERT INTO %", requests, ct);
                await transaction.CommitAsync(ct);
            }

            await Task.WhenAny(requests).WaitAsync(ct);
            StopOn500(requests);
            var responses = await Task.WhenAll(requests).WaitAsync(ct);
            StopOn500(requests);
            var statuses = responses.Select(response => (int)response.StatusCode).Order().ToArray();
            Assert.Equal([200, 429], statuses);
            TestContext.Current.TestOutputHelper!.WriteLine($"MODGUD_TEST_SQL_OVERLAP {phase} 2 200 429");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var proof = new FileStream(Path.Combine(directory, $"overlap-{phase}.json"), options);
            JsonSerializer.Serialize(proof, new { Phase = phase, ConcurrentStatements = 2, StatusCodes = statuses });
        }
        finally
        {
            timeout.Cancel();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the original probe assertion/exception. */ }
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) request.Result.Dispose();
        }
    }

    private static async Task AwaitSqlOverlapAsync(
        NpgsqlConnection monitor, int[] pids, int blockerPid, string prefix,
        Task<HttpResponseMessage>[] requests, CancellationToken ct)
    {
        // Only our database, role and recorded backends; never export query text,
        // PIDs, parameters, other sessions or credential-bearing diagnostics.
        const string sql = """
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND usename = current_user
              AND pid = ANY(@pids) AND state = 'active' AND wait_event_type = 'Lock'
              AND ltrim(query) LIKE @prefix
              AND pg_blocking_pids(pid) && @blockers
            """;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            StopOn500(requests);
            await using var command = new NpgsqlCommand(sql, monitor);
            command.Parameters.AddWithValue("pids", pids);
            command.Parameters.AddWithValue("prefix", prefix);
            command.Parameters.AddWithValue("blockers", pids.Append(blockerPid).ToArray());
            if (await command.ExecuteScalarAsync(ct) is long count && count == 2) return;
            await Task.Delay(25, ct);
        }
        Assert.Fail("No PostgreSQL witness of two overlapping store commands; probe is inconclusive.");
    }

    private static void StopOn500(Task<HttpResponseMessage>[] requests)
    {
        foreach (var request in requests)
        {
            if (!request.IsCompletedSuccessfully || request.Result.StatusCode != HttpStatusCode.InternalServerError)
                continue;
            // Correlation has already persisted only approved exception metadata.
            // No response body or exception message is included in this failure.
            Assert.Fail("Correlated HTTP500: stop before any further probe pair.");
        }
    }

    private sealed class ObservedConnections(IRateLimitConnectionSource inner) : IRateLimitConnectionSource
    {
        private readonly List<int> _pids = [];
        public TaskCompletionSource PairOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int[] Pids { get { lock (_pids) return _pids.ToArray(); } }

        public async Task<NpgsqlConnection> OpenAsync(RateLimitScope scope, CancellationToken ct = default)
        {
            var connection = await inner.OpenAsync(scope, ct);
            lock (_pids)
            {
                _pids.Add(connection.ProcessID);
                if (_pids.Count == 2) PairOpened.TrySetResult();
            }
            return connection;
        }
    }
}
