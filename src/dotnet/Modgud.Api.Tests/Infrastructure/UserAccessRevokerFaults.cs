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

    public void Reset() => FailRevokes = 0;
}

internal sealed class FaultInjectingUserAccessRevoker(IUserAccessRevoker inner, UserAccessRevokerFaults faults) : IUserAccessRevoker
{
    public Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default) =>
        faults.TryConsume()
            ? throw new InvalidOperationException("Injected failure of the user access revoke.")
            : inner.RevokeAllAccessAsync(userId, reason, ct);
}
