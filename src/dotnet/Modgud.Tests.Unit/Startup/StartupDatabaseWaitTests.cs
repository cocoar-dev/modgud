using System.Net.Sockets;
using Modgud.Api.Startup;
using Npgsql;

namespace Modgud.Tests.Unit.Startup;

/// <summary>
/// The bounded Postgres wait at boot: transient "not there yet" failures are
/// retried with growing delays inside the window, configuration errors fail
/// at once, and the window is honoured.
/// </summary>
public class StartupDatabaseWaitTests
{
    /// <summary>Manual clock: only moves when a test's fake delay advances it.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 4, 5, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static NpgsqlException Refused() =>
        new("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

    private static NpgsqlException DnsFailure() =>
        new("Failed to connect", new SocketException((int)SocketError.HostNotFound));

    private static PostgresException Pg(string sqlState) =>
        new("boom", "FATAL", "FATAL", sqlState);

    [Fact]
    public async Task Succeeds_once_postgres_answers()
    {
        var clock = new FakeTimeProvider();
        var delays = new List<TimeSpan>();
        var calls = 0;

        await StartupDatabaseWait.RunAsync(
            _ => ++calls < 4 ? throw Refused() : Task.CompletedTask,
            TimeSpan.FromSeconds(90),
            onRetry: (_, _, _) => { },
            delay: (d, _) => { delays.Add(d); clock.Advance(d); return Task.CompletedTask; },
            clock: clock);

        Assert.Equal(4, calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Fact]
    public async Task Gives_up_after_the_window_and_rethrows_the_last_failure()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        var retries = 0;

        var ex = await Assert.ThrowsAsync<NpgsqlException>(() => StartupDatabaseWait.RunAsync(
            _ => { calls++; throw DnsFailure(); },
            TimeSpan.FromSeconds(30),
            onRetry: (_, _, _) => retries++,
            delay: (d, _) => { clock.Advance(d); return Task.CompletedTask; },
            clock: clock));

        Assert.IsType<SocketException>(ex.InnerException);
        // 1 + 2 + 4 + 8 + 10 = 25 s, then a 5 s remainder, then the deadline.
        Assert.Equal(7, calls);
        Assert.Equal(6, retries);
    }

    [Fact]
    public async Task Delay_never_overshoots_the_deadline_and_caps_at_ten_seconds()
    {
        var clock = new FakeTimeProvider();
        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<NpgsqlException>(() => StartupDatabaseWait.RunAsync(
            _ => throw Refused(),
            TimeSpan.FromSeconds(40),
            onRetry: (_, _, _) => { },
            delay: (d, _) => { delays.Add(d); clock.Advance(d); return Task.CompletedTask; },
            clock: clock));

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
             TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)],
            delays);
        Assert.Equal(TimeSpan.FromSeconds(40), delays.Aggregate(TimeSpan.Zero, (a, b) => a + b));
    }

    [Fact]
    public async Task Zero_window_means_a_single_attempt()
    {
        var calls = 0;
        await Assert.ThrowsAsync<NpgsqlException>(() => StartupDatabaseWait.RunAsync(
            _ => { calls++; throw Refused(); },
            TimeSpan.Zero,
            onRetry: (_, _, _) => Assert.Fail("must not retry"),
            delay: (_, _) => Task.CompletedTask,
            clock: new FakeTimeProvider()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Configuration_errors_fail_immediately()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => StartupDatabaseWait.RunAsync(
            _ => { calls++; throw Pg("28P01"); },   // password authentication failed
            TimeSpan.FromSeconds(90),
            onRetry: (_, _, _) => Assert.Fail("must not retry an auth failure"),
            delay: (_, _) => Task.CompletedTask,
            clock: new FakeTimeProvider()));
        Assert.Equal("28P01", ex.SqlState);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("57P03", true)]   // the database system is starting up
    [InlineData("53300", true)]   // too many connections
    [InlineData("08006", true)]   // connection failure
    [InlineData("28P01", false)]  // password authentication failed
    [InlineData("3D000", false)]  // database does not exist
    [InlineData("42501", false)]  // insufficient privilege
    public void Classifies_postgres_errors(string sqlState, bool transient)
    {
        Assert.Equal(transient, StartupDatabaseWait.IsTransient(Pg(sqlState)));
    }

    [Fact]
    public void Classifies_network_errors_as_transient_and_everything_else_as_fatal()
    {
        Assert.True(StartupDatabaseWait.IsTransient(Refused()));
        Assert.True(StartupDatabaseWait.IsTransient(DnsFailure()));
        Assert.True(StartupDatabaseWait.IsTransient(new TimeoutException()));
        Assert.False(StartupDatabaseWait.IsTransient(new InvalidOperationException()));
        Assert.False(StartupDatabaseWait.IsTransient(new NpgsqlException("bad", new ArgumentException())));
    }
}
