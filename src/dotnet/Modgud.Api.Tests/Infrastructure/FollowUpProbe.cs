using System.Collections.Concurrent;
using Marten;
using Modgud.Infrastructure.FollowUps;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Test-only follow-up: proves the outbox foundation (commit, rollback, realm, retry) without
/// a production effect. <paramref name="FailTimes"/> is the fault seam — the handler throws on
/// that many attempts before it succeeds.
/// </summary>
public sealed record FollowUpProbe(Guid Id, int FailTimes = 0) : IFollowUpMessage;

/// <summary>Records every attempt and every success of <see cref="FollowUpProbe"/>, with the
/// realm of the session the handler got.</summary>
public sealed class FollowUpProbeRecorder
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<string>> _handled = new();

    public int Attempt(Guid id) => _attempts.AddOrUpdate(id, 1, (_, n) => n + 1);

    public void Handled(Guid id, string tenant) => _handled.GetOrAdd(id, _ => []).Add(tenant);

    public int Attempts(Guid id) => _attempts.TryGetValue(id, out var n) ? n : 0;

    public IReadOnlyList<string> HandledIn(Guid id) => _handled.TryGetValue(id, out var bag) ? [.. bag] : [];
}

public static class FollowUpProbeHandler
{
    public static void Handle(FollowUpProbe message, IDocumentSession session, FollowUpProbeRecorder recorder)
    {
        if (recorder.Attempt(message.Id) <= message.FailTimes)
            throw new InvalidOperationException("Injected failure of a follow-up probe.");
        recorder.Handled(message.Id, session.TenantId);
    }
}
