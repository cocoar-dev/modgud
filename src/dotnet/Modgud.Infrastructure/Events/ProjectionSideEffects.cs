using System.Collections.Concurrent;

namespace Modgud.Infrastructure.Events;

/// <summary>
/// Guard for projection side effects (SignalR dispatch via Wolverine).
///
/// <para><b>Global gate</b> (<see cref="Enabled"/>): must be enabled only after
/// Wolverine has started, to avoid WolverineHasNotStartedException during daemon
/// catch-up on startup.</para>
///
/// <para><b>Per-tenant rebuild suppression</b> (audit M8): a projection rebuild
/// replays a tenant's whole event history and must NOT spam SignalR with a side
/// effect per historical event — but the suppression has to be scoped to the
/// tenant being rebuilt. The previous global <c>Enabled=false</c> toggle froze
/// side effects for EVERY realm for the duration of one realm's rebuild, silently
/// dropping other tenants' live updates. The rebuild endpoint now suppresses only
/// its own tenant via <see cref="SuppressRebuildFor"/> / <see cref="ResumeAfterRebuild"/>,
/// and projections gate on <see cref="IsEnabledFor"/>.</para>
/// </summary>
public static class ProjectionSideEffects
{
    /// <summary>Global startup gate — side effects stay off until Wolverine has started.</summary>
    public static bool Enabled { get; set; }

    // Tenants currently mid-rebuild. Concurrent rebuilds are serialised by the
    // endpoint, but reads happen from projection threads, so keep it thread-safe.
    private static readonly ConcurrentDictionary<string, byte> Rebuilding = new(StringComparer.Ordinal);

    /// <summary>Suppress side effects for one tenant while its projections are rebuilt.</summary>
    public static void SuppressRebuildFor(string tenantId) => Rebuilding[tenantId] = 0;

    /// <summary>Lift the rebuild suppression for one tenant.</summary>
    public static void ResumeAfterRebuild(string tenantId) => Rebuilding.TryRemove(tenantId, out _);

    /// <summary>
    /// Side effects fire only when globally enabled AND the given tenant is not
    /// mid-rebuild. A null tenant (shouldn't happen for tenant-scoped projections)
    /// falls back to the global gate.
    /// </summary>
    public static bool IsEnabledFor(string? tenantId)
        => Enabled && (string.IsNullOrEmpty(tenantId) || !Rebuilding.ContainsKey(tenantId));
}
