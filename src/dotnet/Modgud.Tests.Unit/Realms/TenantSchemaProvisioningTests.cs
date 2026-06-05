using Modgud.Infrastructure.Realms;
using Npgsql;

namespace Modgud.Tests.Unit.Realms;

/// <summary>
/// Pins the resilient tenant-schema apply that tolerates the async-daemon race during
/// realm provisioning (see <see cref="TenantSchemaProvisioning"/>). The race itself is
/// non-deterministic — in a live host the async daemon may or may not win the lock — so
/// these tests drive the retry mechanism directly with a fake apply delegate instead of
/// trying to reproduce the race in an integration test.
/// </summary>
public class TenantSchemaProvisioningTests
{
    private static PostgresException Pg(string sqlState) =>
        new("simulated", "ERROR", "ERROR", sqlState);

    // Zero backoff so the retry tests run instantly.
    private static readonly Func<int, TimeSpan> NoDelay = _ => TimeSpan.Zero;

    public class ConflictDetection
    {
        [Fact]
        public void Detects_a_direct_duplicate_object_conflict()
        {
            var found = TenantSchemaProvisioning.TryFindConcurrentSchemaConflict(
                Pg(PostgresErrorCodes.DuplicateObject), out var conflict);

            Assert.True(found);
            Assert.Equal(PostgresErrorCodes.DuplicateObject, conflict!.SqlState);
        }

        [Fact]
        public void Detects_a_conflict_wrapped_the_way_marten_wraps_it()
        {
            // Marten surfaces the driver error as MartenSchemaException -> PostgresException;
            // a plain wrapper exception exercises the same inner-exception walk.
            var wrapped = new Exception(
                "DDL Execution for 'All Configured Changes' Failed!",
                Pg(PostgresErrorCodes.DuplicateTable));

            var found = TenantSchemaProvisioning.TryFindConcurrentSchemaConflict(wrapped, out var conflict);

            Assert.True(found);
            Assert.Equal(PostgresErrorCodes.DuplicateTable, conflict!.SqlState);
        }

        [Fact]
        public void Ignores_an_unrelated_postgres_error()
        {
            // 42P01 undefined_table is a genuine error, not a benign concurrent-create.
            var found = TenantSchemaProvisioning.TryFindConcurrentSchemaConflict(
                Pg(PostgresErrorCodes.UndefinedTable), out var conflict);

            Assert.False(found);
            Assert.Null(conflict);
        }

        [Fact]
        public void Ignores_a_non_postgres_exception()
        {
            var found = TenantSchemaProvisioning.TryFindConcurrentSchemaConflict(
                new InvalidOperationException("boom"), out var conflict);

            Assert.False(found);
            Assert.Null(conflict);
        }
    }

    public class RetryBehaviour
    {
        [Fact]
        public async Task Retries_a_transient_conflict_then_succeeds()
        {
            var attempts = 0;
            var retries = new List<int>();

            await TenantSchemaProvisioning.ApplyWithRetryAsync(
                apply: () =>
                {
                    attempts++;
                    if (attempts == 1)
                        throw new Exception("wrapped", Pg(PostgresErrorCodes.DuplicateObject));
                    return Task.CompletedTask;
                },
                maxAttempts: 5,
                backoff: NoDelay,
                onRetry: (_, attempt) => retries.Add(attempt),
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(2, attempts);   // failed once, then succeeded on the retry
            Assert.Equal([1], retries);  // onRetry fired exactly once, for attempt 1
        }

        [Fact]
        public async Task Gives_up_and_rethrows_after_maxAttempts()
        {
            var attempts = 0;
            var retries = 0;

            var thrown = await Assert.ThrowsAsync<Exception>(() =>
                TenantSchemaProvisioning.ApplyWithRetryAsync(
                    apply: () =>
                    {
                        attempts++;
                        throw new Exception("wrapped", Pg(PostgresErrorCodes.DuplicateObject));
                    },
                    maxAttempts: 3,
                    backoff: NoDelay,
                    onRetry: (_, _) => retries++,
                    ct: TestContext.Current.CancellationToken));

            Assert.Equal(3, attempts);  // tried maxAttempts times
            Assert.Equal(2, retries);   // retried after attempts 1 and 2, then gave up
            Assert.True(TenantSchemaProvisioning.TryFindConcurrentSchemaConflict(thrown, out _));
        }

        [Fact]
        public async Task Does_not_retry_a_non_conflict_error()
        {
            var attempts = 0;
            var retries = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TenantSchemaProvisioning.ApplyWithRetryAsync(
                    apply: () =>
                    {
                        attempts++;
                        throw new InvalidOperationException("not a schema conflict");
                    },
                    maxAttempts: 5,
                    backoff: NoDelay,
                    onRetry: (_, _) => retries++,
                    ct: TestContext.Current.CancellationToken));

            Assert.Equal(1, attempts);  // bailed immediately, no retry
            Assert.Equal(0, retries);
        }

        [Fact]
        public async Task Succeeds_without_retry_when_apply_works_first_time()
        {
            var attempts = 0;
            var retries = 0;

            await TenantSchemaProvisioning.ApplyWithRetryAsync(
                apply: () => { attempts++; return Task.CompletedTask; },
                maxAttempts: 5,
                backoff: NoDelay,
                onRetry: (_, _) => retries++,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(1, attempts);
            Assert.Equal(0, retries);
        }
    }
}
