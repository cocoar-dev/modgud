using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace Modgud.Infrastructure.Cluster;

/// <summary>One live node of this deployment as Wolverine's node table sees it.</summary>
public sealed record ClusterNodeInfo(
    Guid NodeId,
    int NodeNumber,
    string Description,
    DateTimeOffset Started,
    DateTimeOffset LastHealthCheck,
    bool IsLeader,
    bool IsSelf);

/// <summary>
/// Answers "how many of us are running right now?" from the one place that
/// already knows: Wolverine's node table in the master database (ADR 0022, D2).
/// There is no instance-count setting to keep in sync with reality; readiness,
/// the projection-rebuild guard and the admin UI all read this.
/// </summary>
public interface IClusterNodes
{
    /// <summary>This node's Wolverine id.</summary>
    Guid LocalNodeId { get; }

    /// <summary>Human-readable name of this node (container/machine name).</summary>
    string LocalNodeName { get; }

    /// <summary>
    /// Nodes whose last heartbeat is younger than Wolverine's stale-node timeout.
    /// Always contains at least the local node. Under single-process coordination
    /// the answer is exactly the local node without touching the database.
    /// </summary>
    Task<IReadOnlyList<ClusterNodeInfo>> GetLiveNodesAsync(CancellationToken ct = default);

    /// <summary>
    /// B9-CL01 — like <see cref="GetLiveNodesAsync"/>, but <c>null</c> when the node table
    /// could not be read. A guard that needs proof of "only this instance" (projection
    /// rebuild) must not treat an unknown topology as a single node; readiness keeps the
    /// "just me" fallback of <see cref="GetLiveNodesAsync"/>.
    /// </summary>
    async Task<IReadOnlyList<ClusterNodeInfo>?> TryGetLiveNodesAsync(CancellationToken ct = default)
        => await GetLiveNodesAsync(ct);
}

internal sealed class WolverineClusterNodes(
    IWolverineRuntime runtime,
    ClusterHostingOptions hosting,
    TimeProvider clock,
    ILogger<WolverineClusterNodes> logger) : IClusterNodes
{
    public Guid LocalNodeId => runtime.Options.UniqueNodeId;

    public string LocalNodeName => hosting.NodeName;

    public async Task<IReadOnlyList<ClusterNodeInfo>> GetLiveNodesAsync(CancellationToken ct = default)
        => await LoadAsync(fallBackToSelf: true, ct) ?? [];

    public Task<IReadOnlyList<ClusterNodeInfo>?> TryGetLiveNodesAsync(CancellationToken ct = default)
        => LoadAsync(fallBackToSelf: false, ct);

    private async Task<IReadOnlyList<ClusterNodeInfo>?> LoadAsync(bool fallBackToSelf, CancellationToken ct)
    {
        var self = new ClusterNodeInfo(
            LocalNodeId,
            runtime.Options.Durability.AssignedNodeNumber,
            hosting.NodeName,
            Started: runtime.Options.Durability.Mode == DurabilityMode.Solo ? clock.GetUtcNow() : DateTimeOffset.MinValue,
            LastHealthCheck: clock.GetUtcNow(),
            IsLeader: true,
            IsSelf: true);

        if (!hosting.IsWolverineManaged)
            return [self];

        try
        {
            var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(ct);
            var now = clock.GetUtcNow();
            var staleAfter = runtime.Options.Durability.StaleNodeTimeout;

            var live = nodes
                .Where(n => n.NodeId == LocalNodeId || now - n.LastHealthCheck <= staleAfter)
                .Select(n => new ClusterNodeInfo(
                    n.NodeId,
                    n.AssignedNodeNumber,
                    n.NodeId == LocalNodeId ? hosting.NodeName : n.Description,
                    n.Started,
                    n.LastHealthCheck,
                    n.IsLeader(),
                    n.NodeId == LocalNodeId))
                .OrderBy(n => n.NodeNumber)
                .ToList();

            // Wolverine registers this node asynchronously after host start; until
            // the row exists the table may not list us yet. Never report zero.
            if (live.All(n => !n.IsSelf))
                live.Insert(0, self);

            return live;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failing node query must not take readiness down by itself — the
            // Postgres probe reports the database. Fall back to "just me" and say so.
            if (!fallBackToSelf)
            {
                logger.LogWarning(ex, "Could not read the Wolverine node table; the cluster topology is unknown");
                return null;
            }
            logger.LogWarning(ex, "Could not read the Wolverine node table; assuming this is the only live node");
            return [self];
        }
    }
}
