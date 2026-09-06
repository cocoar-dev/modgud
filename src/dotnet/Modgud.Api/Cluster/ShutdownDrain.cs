namespace Modgud.Api.Cluster;

/// <summary>
/// Process-wide "we are shutting down" flag. Readiness reads it so the reverse
/// proxy stops routing to this node before Kestrel closes its listener
/// (ADR 0022, D7).
/// </summary>
public sealed class ShutdownState
{
    private int _stopping;

    public bool IsStopping => Volatile.Read(ref _stopping) == 1;

    internal void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);
}

/// <summary>
/// Graceful drain. On <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// the readiness probe flips to 503 immediately; this service then holds the
/// shutdown for the configured delay so active health checks at the proxy
/// (Caddy every 5 s, Docker every 15 s) observe the 503 and drain traffic away
/// while in-flight requests complete. Blocking inside the stopping callback is
/// the documented way to delay the host: the callbacks run synchronously before
/// the server is told to stop.
/// </summary>
public sealed class ShutdownDrainService(
    IHostApplicationLifetime lifetime,
    ShutdownState state,
    TimeSpan drainDelay,
    ILogger<ShutdownDrainService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopping.Register(() =>
        {
            state.MarkStopping();
            if (drainDelay <= TimeSpan.Zero) return;

            logger.LogInformation(
                "Shutdown requested — readiness now reports 503; draining for {Seconds}s before the listener closes",
                (int)drainDelay.TotalSeconds);
            Thread.Sleep(drainDelay);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
