using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Test seam for G-B4-02: makes the next <see cref="FailAuthorizationRevokes"/> calls of
/// <see cref="IOAuthGrantRevoker.RevokeAuthorizationByIdAsync"/> throw, the way a database or
/// OpenIddict failure would after the staffing end was committed. Reset between test classes.
/// </summary>
public sealed class GrantRevokerFaults
{
    private int _failAuthorizationRevokes;

    public int FailAuthorizationRevokes
    {
        get => Volatile.Read(ref _failAuthorizationRevokes);
        set => Volatile.Write(ref _failAuthorizationRevokes, value);
    }

    private int _failSubjectTokenRevokes;

    /// <summary>B7-SC05 — the next n <see cref="IOAuthGrantRevoker.RevokeTokensBySubjectAsync"/> calls throw.</summary>
    public int FailSubjectTokenRevokes
    {
        get => Volatile.Read(ref _failSubjectTokenRevokes);
        set => Volatile.Write(ref _failSubjectTokenRevokes, value);
    }

    public bool TryConsumeSubjectTokenRevokeFault() => TryConsume(ref _failSubjectTokenRevokes);

    private static bool TryConsume(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return true;
        }
    }

    public bool TryConsumeAuthorizationRevokeFault()
    {
        while (true)
        {
            var current = Volatile.Read(ref _failAuthorizationRevokes);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _failAuthorizationRevokes, current - 1, current) == current) return true;
        }
    }

    public void Reset()
    {
        FailAuthorizationRevokes = 0;
        FailSubjectTokenRevokes = 0;
    }
}

internal sealed class FaultInjectingGrantRevoker(IOAuthGrantRevoker inner, GrantRevokerFaults faults) : IOAuthGrantRevoker
{
    public Task<int> RevokeTokensBySubjectAsync(string subject, CancellationToken ct = default) =>
        faults.TryConsumeSubjectTokenRevokeFault()
            ? throw new InvalidOperationException("Injected failure of the subject token revoke.")
            : inner.RevokeTokensBySubjectAsync(subject, ct);
    public Task<int> RevokeAuthorizationsBySubjectAsync(string subject, CancellationToken ct = default) => inner.RevokeAuthorizationsBySubjectAsync(subject, ct);
    public Task<int> RevokeTokensByApplicationIdAsync(string applicationId, CancellationToken ct = default) => inner.RevokeTokensByApplicationIdAsync(applicationId, ct);
    public Task<int> RevokeAuthorizationsByApplicationIdAsync(string applicationId, CancellationToken ct = default) => inner.RevokeAuthorizationsByApplicationIdAsync(applicationId, ct);
    public Task<int> RevokeTokensByAuthorizationIdAsync(string authorizationId, CancellationToken ct = default) => inner.RevokeTokensByAuthorizationIdAsync(authorizationId, ct);

    public Task<bool> RevokeAuthorizationByIdAsync(string authorizationId, CancellationToken ct = default) =>
        faults.TryConsumeAuthorizationRevokeFault()
            ? throw new InvalidOperationException("Injected failure of the authorization revoke.")
            : inner.RevokeAuthorizationByIdAsync(authorizationId, ct);
}
