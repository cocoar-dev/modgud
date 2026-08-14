using JasperFx.Events.Daemon;
using Modgud.Infrastructure.Persistence.Marten;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Marten;

public sealed class ProjectionCoordinatorControlTests
{
    [Fact]
    public async Task Normal_maintenance_pauses_runs_and_resumes_in_order()
    {
        var coordinator = new RecordingCoordinator();
        using var control = new HostedProjectionCoordinatorControl(coordinator);

        await control.RunPausedAsync(_ =>
        {
            coordinator.Calls.Add("Action");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["Pause", "Action", "Resume"], coordinator.Calls);
    }

    [Fact]
    public async Task Host_stop_drains_inflight_maintenance_and_prevents_late_resume()
    {
        var coordinator = new RecordingCoordinator();
        using var control = new HostedProjectionCoordinatorControl(coordinator);
        var actionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var maintenance = control.RunPausedAsync(async _ =>
        {
            coordinator.Calls.Add("Action");
            actionEntered.SetResult();
            await releaseAction.Task;
        }, TestContext.Current.CancellationToken);

        await actionEntered.Task;
        var hostStop = control.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(hostStop.IsCompleted);
        releaseAction.SetResult();

        await maintenance;
        await hostStop;

        Assert.Equal(["Pause", "Action"], coordinator.Calls);
    }

    private sealed class RecordingCoordinator
        : global::Marten.Events.Daemon.Coordination.IProjectionCoordinator
    {
        public List<string> Calls { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Stop");
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            Calls.Add("Pause");
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            Calls.Add("Resume");
            return Task.CompletedTask;
        }

        public IProjectionDaemon DaemonForMainDatabase() => throw new NotSupportedException();

        public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
            => throw new NotSupportedException();
    }
}
