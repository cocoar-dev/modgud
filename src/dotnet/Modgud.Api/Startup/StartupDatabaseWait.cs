using System.Net.Sockets;
using Npgsql;

namespace Modgud.Api.Startup;

/// <summary>
/// Bounded wait for PostgreSQL at boot. A container stack starts Modgud and
/// Postgres side by side, and Modgud regularly wins that race: the first
/// connect fails with "connection refused", "name or service not known" or
/// Postgres' own <c>57P03 the database system is starting up</c>. Those are
/// transient and worth waiting for. A wrong password, a missing role or a
/// broken connection string are not — they fail on the first attempt so the
/// operator sees the real cause immediately instead of after the window.
///
/// <para>After the window the last exception is rethrown and the host
/// terminates (see the fatal path in <c>Program.cs</c>). The wait never
/// serves traffic: Kestrel only starts after the bootstrap succeeded.</para>
/// </summary>
public static class StartupDatabaseWait
{
    /// <summary>Default window: long enough for a cold Postgres container, short enough to notice a dead one.</summary>
    public const int DefaultTimeoutSeconds = 90;

    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="attempt"/> until it succeeds, a non-transient error
    /// occurs, or <paramref name="window"/> elapses. Delays grow 1 s, 2 s, 4 s,
    /// 8 s and then stay at 10 s. A window of zero means "exactly one attempt".
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> attempt,
        TimeSpan window,
        Action<int, TimeSpan, Exception> onRetry,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? clock = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(onRetry);
        delay ??= Task.Delay;
        clock ??= TimeProvider.System;

        var deadline = clock.GetUtcNow() + window;
        var attemptNo = 0;
        var nextDelay = TimeSpan.FromSeconds(1);

        while (true)
        {
            attemptNo++;
            try
            {
                await attempt(ct);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                var remaining = deadline - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    throw;

                var wait = nextDelay < remaining ? nextDelay : remaining;
                onRetry(attemptNo, wait, ex);
                await delay(wait, ct);
                nextDelay = nextDelay * 2 > MaxDelay ? MaxDelay : nextDelay * 2;
            }
        }
    }

    /// <summary>
    /// "Postgres is not there yet" — worth retrying. Everything else (auth,
    /// missing database, syntax, permissions) is a configuration error and
    /// must surface at once.
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        switch (ex)
        {
            case PostgresException pg:
                // 57P03 the database system is starting up / shutting down,
                // 53300 too many connections (pool warm-up race),
                // 08xxx connection exceptions.
                return pg.SqlState is "57P03" or "57P02" or "57P01" or "53300"
                       || pg.SqlState.StartsWith("08", StringComparison.Ordinal);
            case NpgsqlException npg:
                // Network-level failure before Postgres answered (refused, DNS,
                // reset) or a timeout while it was still coming up.
                return npg.IsTransient
                       || npg.InnerException is SocketException or TimeoutException or IOException
                       || npg.InnerException is null;
            case SocketException:
            case TimeoutException:
                return true;
            default:
                return false;
        }
    }
}
