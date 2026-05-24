namespace Cocoar.Auth.Infrastructure.Scheduling;

/// <summary>
/// Cross-slice seam — invoked by <see cref="JobRunListener"/> after a job run
/// finishes, before the request scope is disposed. The implementation lives
/// in <c>Cocoar.Auth.Api.Features.Inbox</c> where it has access to both
/// <c>IInboxNotifier</c> (Application/Inbox) and <c>IAdminNotifier</c>
/// (Authentication slice). Keeping the interface here lets JobRunListener
/// depend on it without dragging the inbox + admin slices into Infrastructure.
///
/// <para>The default no-op implementation registered in
/// <c>SchedulingDependencyInjection</c> means hosts without Inbox wiring
/// (e.g. tests, future minimal forks) work without an extra registration —
/// the Api-side wiring just overrides the binding with the real notifier.</para>
/// </summary>
public interface IJobRunNotifier
{
    Task NotifyAsync(JobRunHistoryEntry entry, CancellationToken ct = default);
}

/// <summary>No-op default — overridden in Api when Inbox wiring is in.</summary>
internal sealed class NoopJobRunNotifier : IJobRunNotifier
{
    public Task NotifyAsync(JobRunHistoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
}
