using System.Collections.Concurrent;
using Modgud.Authentication.Sessions;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Test seam for B4-AP-01: makes the next <see cref="FailRevokes"/> calls of
/// <see cref="IUserAccessRevoker.RevokeAllAccessAsync"/> throw, the way a failing token, session or
/// security-stamp step would after the user change was committed. Reset between test classes.
/// </summary>
public sealed class UserAccessRevokerFaults
{
    private int _failRevokes;

    public int FailRevokes
    {
        get => Volatile.Read(ref _failRevokes);
        set => Volatile.Write(ref _failRevokes, value);
    }

    public bool TryConsume()
    {
        while (true)
        {
            var current = Volatile.Read(ref _failRevokes);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _failRevokes, current - 1, current) == current) return true;
        }
    }

    /// <summary>Every revoke that got past the fault, with when it ran.</summary>
    public ConcurrentQueue<(Guid UserId, DateTimeOffset Started, DateTimeOffset Ended)> Calls { get; } = new();

    public void Reset()
    {
        FailRevokes = 0;
        Calls.Clear();
    }
}

internal sealed class FaultInjectingUserAccessRevoker(IUserAccessRevoker inner, UserAccessRevokerFaults faults) : IUserAccessRevoker
{
    public async Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default)
    {
        if (faults.TryConsume())
            throw new InvalidOperationException("Injected failure of the user access revoke.");
        var started = DateTimeOffset.UtcNow;
        await inner.RevokeAllAccessAsync(userId, reason, ct);
        faults.Calls.Enqueue((userId, started, DateTimeOffset.UtcNow));
    }
}
