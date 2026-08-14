using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.Hosting;

namespace Modgud.Infrastructure.Persistence.Marten;

/// <summary>
/// Owns every application-initiated pause/stop window for Marten's async daemon.
/// Production uses the hosted implementation to serialize maintenance against host
/// shutdown. Deterministic test hosts use the disabled implementation and drive a
/// fresh interactive daemon at explicit projection barriers instead.
/// </summary>
public interface IProjectionCoordinatorControl
{
    bool IsBackgroundDaemonEnabled { get; }

    Task RunPausedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task RunStoppedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}

internal sealed class HostedProjectionCoordinatorControl(
    IProjectionCoordinator coordinator) : IProjectionCoordinatorControl, IHostedService, IDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _stopping;
    private int _disposed;
    private bool _shutdownLeaseHeld;

    public bool IsBackgroundDaemonEnabled => true;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        // This hosted service is registered after Marten's coordinator, so the
        // generic host stops it first. Keep the lease until container disposal:
        // all application maintenance has then drained before the host invokes the
        // coordinator's own StopAsync. This avoids JasperFx 2.47's unsynchronised
        // Start/Pause/Stop fields racing during shutdown.
        await _lifecycleGate.WaitAsync(cancellationToken);
        _shutdownLeaseHeld = true;
    }

    public Task RunPausedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(stopCoordinator: false, action, cancellationToken);

    public Task RunStoppedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(stopCoordinator: true, action, cancellationToken);

    private async Task RunExclusiveAsync(
        bool stopCoordinator,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
                throw new OperationCanceledException(
                    "Projection maintenance cannot start while the host is stopping.",
                    cancellationToken);

            if (stopCoordinator)
                await coordinator.StopAsync(cancellationToken);
            else
                await coordinator.PauseAsync();

            try
            {
                await action(cancellationToken);
            }
            finally
            {
                // Host shutdown owns the coordinator from this point onward. A
                // late ResumeAsync would race its StopAsync on JasperFx's internal
                // cancellation source and can resurrect the leadership loop.
                if (Volatile.Read(ref _stopping) == 0)
                    await coordinator.ResumeAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_shutdownLeaseHeld)
        {
            _shutdownLeaseHeld = false;
            _lifecycleGate.Release();
        }

        _lifecycleGate.Dispose();
    }
}

internal sealed class DisabledProjectionCoordinatorControl : IProjectionCoordinatorControl
{
    public bool IsBackgroundDaemonEnabled => false;

    public Task RunPausedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => RunAsync(action, cancellationToken);

    public Task RunStoppedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => RunAsync(action, cancellationToken);

    private static Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return action(cancellationToken);
    }
}
