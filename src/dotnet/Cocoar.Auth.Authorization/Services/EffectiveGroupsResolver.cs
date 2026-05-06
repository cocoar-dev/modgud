using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Marten;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Resolves the effective groups of a principal by combining the existing
/// <see cref="IPermissionService.GetUserGroupsAsync"/> BFS (which covers
/// Direct/Inherited via materialized MemberIds) with a live evaluation of
/// every Auto-mode group's compiled membership script. Failed scripts are
/// reported as diagnostics rather than thrown — admins use this surface to
/// debug why a user does or does not match an auto-group.
/// </summary>
public class EffectiveGroupsResolver(
    IQuerySession session,
    IPermissionService permissionService,
    IMembershipEvaluator evaluator,
    ILogger<EffectiveGroupsResolver> logger) : IEffectiveGroupsResolver
{
    public async Task<EffectiveGroupsResult> ResolveAsync(Guid principalId, CancellationToken ct = default)
    {
        var principal = await session.LoadAsync<Principal>(principalId, ct);
        if (principal is null || principal.IsDeleted)
        {
            return new EffectiveGroupsResult(principalId, [], []);
        }

        // Reuse the existing BFS for materialized direct + inherited memberships.
        // It walks Group.MemberIds upward starting from the principal — i.e. it
        // surfaces every group the principal is materialized into (Manual or
        // Auto-with-MemberIds-set). Crucially it does NOT live-evaluate scripts.
        var materializedGroups = await permissionService.GetUserGroupsAsync(principalId, ct);

        // Pull all non-deleted groups once — we need every Auto group anyway
        // for the live script evaluation pass and we already have an in-memory
        // structure from the BFS, so a second query for the same data would be
        // wasteful.
        var allGroups = await session.Query<Group>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct);
        var groupsById = allGroups.ToDictionary(g => g.Id);

        // Roles are looked up once and cached so each group row gets its role
        // names without N+1 queries.
        var roleIds = allGroups.SelectMany(g => g.RoleIds).Distinct().ToArray();
        var rolesById = roleIds.Length == 0
            ? new Dictionary<Guid, PermissionRole>()
            : (await session.Query<PermissionRole>()
                .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
                .ToListAsync(ct))
                .ToDictionary(r => r.Id);

        // Source-resolution policy: a group can qualify under multiple sources
        // (e.g. it is Auto and the principal is also in MemberIds via stale
        // materialization). The highest-precedence source wins, in this order:
        //   1. DirectManual    — strongest signal: admin explicitly listed them
        //   2. InheritedManual — via a chain of manual groups
        //   3. AutoMatched     — the predicate matches right now
        var rows = new Dictionary<Guid, EffectiveGroupRow>();

        // ── 1. DirectManual + InheritedManual via the materialized BFS ──────
        // The BFS already returns every materialized ancestor — we then split
        // them by whether the principal is in their MemberIds directly (Direct)
        // or only via a manual chain (Inherited).
        var directIds = allGroups
            .Where(g => g.MemberIds.Contains(principalId))
            .Select(g => g.Id)
            .ToHashSet();

        // For Inherited rows we want a Via chain — re-run the lightweight
        // upward BFS that the /api/user/{id}/groups endpoint uses, so the
        // emitted Via mirrors that endpoint's semantics.
        var inheritedVia = BuildInheritedViaMap(allGroups, principalId, directIds);

        foreach (var group in materializedGroups)
        {
            if (directIds.Contains(group.Id))
            {
                // Manual + direct: highest precedence among materialized signals.
                // An Auto group that happens to list the principal is treated as
                // DirectManual here only if it really is Manual — Auto groups whose
                // MemberIds contain the principal still surface, but as AutoMatched
                // (or stale-AutoMatched) below; we only call this DirectManual when
                // the membership mode is Manual.
                var source = group.MembershipMode == MembershipMode.Manual
                    ? EffectiveGroupSource.DirectManual
                    : EffectiveGroupSource.AutoMatched; // handled in the auto pass
                if (source == EffectiveGroupSource.DirectManual)
                {
                    rows[group.Id] = BuildRow(group, source, via: null, materializedMatches: null, rolesById);
                }
            }
            else if (inheritedVia.TryGetValue(group.Id, out var via))
            {
                rows[group.Id] = BuildRow(
                    group, EffectiveGroupSource.InheritedManual, via, materializedMatches: null, rolesById);
            }
        }

        // ── 2. AutoMatched: live-evaluate every Auto group's predicate ──────
        var diagnostics = new List<GroupDiagnostic>();
        foreach (var group in allGroups)
        {
            if (group.MembershipMode != MembershipMode.Auto) continue;
            if (string.IsNullOrWhiteSpace(group.CompiledMembershipScript)) continue;
            // A group never lists itself — matches AutoMembershipRecalculator's filter.
            if (group.Id == principalId) continue;

            bool matches;
            try
            {
                var compiled = evaluator.BuildPredicate<Principal>(group.CompiledMembershipScript, ct).Compile();
                try
                {
                    matches = compiled(principal);
                }
                catch (Exception ex)
                {
                    var msg = ex.InnerException?.Message is { Length: > 0 } inner
                        ? $"{ex.Message} — {inner}"
                        : ex.Message;
                    logger.LogDebug(ex,
                        "[EffectiveGroups] Membership predicate threw for principal {PrincipalId} on group {GroupId}",
                        principalId, group.Id);
                    diagnostics.Add(new GroupDiagnostic(
                        group.Id, group.Name, GroupDiagnosticKind.EvalFailed, msg));
                    continue;
                }
            }
            catch (Exception ex)
            {
                var msg = ex.InnerException?.Message is { Length: > 0 } inner
                    ? $"{ex.Message} — {inner}"
                    : ex.Message;
                logger.LogDebug(ex,
                    "[EffectiveGroups] Failed to compile membership script for group {GroupId}",
                    group.Id);
                diagnostics.Add(new GroupDiagnostic(
                    group.Id, group.Name, GroupDiagnosticKind.CompileFailed, msg));
                continue;
            }

            if (!matches) continue;

            // The script matches. Stamp materialization-drift signal:
            // MaterializedMatches=false means "predicate says yes, but the
            // principal is not in MemberIds yet" — i.e. someone never recomputed.
            var materializedMatches = group.MemberIds.Contains(principalId);

            // Auto-matched supersedes nothing; only Direct/Inherited Manual win
            // when both are present. If the group is already in rows under a
            // Manual source, leave it alone (Manual precedence).
            if (rows.ContainsKey(group.Id)) continue;

            rows[group.Id] = BuildRow(
                group, EffectiveGroupSource.AutoMatched, via: null, materializedMatches: materializedMatches, rolesById);
        }

        var ordered = rows.Values
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new EffectiveGroupsResult(principalId, ordered, diagnostics);
    }

    private static EffectiveGroupRow BuildRow(
        Group group,
        EffectiveGroupSource source,
        IReadOnlyList<EffectiveGroupViaStep>? via,
        bool? materializedMatches,
        IReadOnlyDictionary<Guid, PermissionRole> rolesById)
    {
        var roles = group.RoleIds
            .Select(id => rolesById.TryGetValue(id, out var r)
                ? new EffectiveGroupRoleRef(r.Id, r.Name)
                : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        return new EffectiveGroupRow(
            group.Id,
            group.Name,
            group.Description,
            roles,
            source,
            via,
            materializedMatches);
    }

    /// <summary>
    /// Mirrors the upward BFS used by <c>GET /api/user/{id}/groups</c>:
    /// for each inherited (non-direct) ancestor group, returns the chain of
    /// hops from the direct group inwards to the ancestor — same shape the
    /// existing endpoint uses for its "Via" hint.
    /// </summary>
    private static Dictionary<Guid, List<EffectiveGroupViaStep>> BuildInheritedViaMap(
        IReadOnlyList<Group> allGroups,
        Guid principalId,
        HashSet<Guid> directIds)
    {
        var directGroups = allGroups.Where(g => directIds.Contains(g.Id)).ToList();
        var visited = new HashSet<Guid>(directIds);
        // Each queue entry carries the chain that reached it, starting at the
        // direct group it entered through.
        var queue = new Queue<(Guid currentId, List<EffectiveGroupViaStep> chain)>();
        foreach (var direct in directGroups)
        {
            queue.Enqueue((direct.Id, new List<EffectiveGroupViaStep>
            {
                new(direct.Id, direct.Name),
            }));
        }

        var result = new Dictionary<Guid, List<EffectiveGroupViaStep>>();
        while (queue.Count > 0)
        {
            var (currentId, chain) = queue.Dequeue();
            foreach (var parent in allGroups.Where(g => g.MemberIds.Contains(currentId)))
            {
                if (parent.Id == principalId) continue; // never list self
                if (!visited.Add(parent.Id)) continue;

                var nextChain = new List<EffectiveGroupViaStep>(chain.Count + 1);
                nextChain.AddRange(chain);
                nextChain.Add(new EffectiveGroupViaStep(parent.Id, parent.Name));
                result[parent.Id] = nextChain;
                queue.Enqueue((parent.Id, nextChain));
            }
        }

        // The chain we want surfaced for the UI: the entry direct group plus
        // intermediate hops, ending with the inherited group itself. We stored
        // the whole walk above, so trim the last element (which is the target
        // group) — the UI cares about how we got there, not the destination.
        return result.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Take(kv.Value.Count - 1).ToList());
    }
}
