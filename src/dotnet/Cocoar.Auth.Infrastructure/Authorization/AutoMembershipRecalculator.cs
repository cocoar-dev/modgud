using Cocoar.Auth.Application.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Marten;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Authorization;

/// <summary>
/// Recomputes the membership of Auto groups.
/// <list type="bullet">
///   <item>For a single group: runs the predicate as a single SQL query against Marten.</item>
///   <item>For a single principal: compiles the predicate in-memory and tests the loaded principal.</item>
/// </list>
/// Appends <see cref="AuthorizationGroupMembershipRecomputedEvent"/> when the resulting
/// MemberIds differ from the current state. The AuthorizationGroupProjection is inline,
/// so changes are visible immediately after SaveChangesAsync.
/// <para>
/// Runs against <see cref="PrincipalDirectory"/> — scripts can author type-aware
/// predicates (e.g. <c>p.Type === 'Person' &amp;&amp; p.Email.endsWith('@x')</c>).
/// </para>
/// </summary>
public class AutoMembershipRecalculator(
    IMembershipEvaluator evaluator,
    ILogger<AutoMembershipRecalculator> logger)
{
    /// <summary>
    /// Recomputes auto-group membership for a single principal (Person or Group) —
    /// triggered whenever a principal-side property changes. Per-group skip via
    /// <paramref name="changedPaths"/> intersected with the stored script dependencies.
    /// <c>null</c> changedPaths = "invalidate-all" (we don't know, re-evaluate every group).
    /// </summary>
    public async Task RecalculateForPrincipalAsync(
        Guid principalId,
        IDocumentSession session,
        IReadOnlyCollection<string>? changedPaths = null,
        CancellationToken ct = default)
    {
        var principal = await session.LoadAsync<PrincipalDirectory>(principalId, ct);

        var groups = await session.Query<AuthorizationGroup>()
            .Where(g => !g.IsDeleted && g.MembershipMode == MembershipMode.Auto)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.CompiledMembershipScript)) continue;

            // A group never lists itself (matches the filter in RecalculateForGroupAsync).
            if (group.Id == principalId) continue;

            // Dependency-driven skip: if the caller tells us which principal paths
            // changed and the script's recorded dependencies don't intersect them,
            // the result can't change. Null deps = "invalidate-all" (stay safe,
            // always evaluate).
            if (changedPaths is { Count: > 0 } &&
                group.MembershipScriptDependencies is { Count: > 0 } deps &&
                !DepsIntersect(deps, changedPaths))
            {
                continue;
            }

            var shouldBeMember = principal is not null
                && !principal.IsDeleted
                && EvaluateSafe(group.CompiledMembershipScript, principal);
            var isMember = group.MemberIds.Contains(principalId);

            if (shouldBeMember == isMember) continue;

            var newMembers = shouldBeMember
                ? [.. group.MemberIds, principalId]
                : group.MemberIds.Where(id => id != principalId).ToList();

            logger.LogInformation(
                "[AutoMembership] Principal {PrincipalId} {Action} group {GroupId} ({GroupName})",
                principalId, shouldBeMember ? "added to" : "removed from", group.Id, group.Name);

            session.Events.Append(group.Id, new AuthorizationGroupMembershipRecomputedEvent(group.Id, newMembers));
        }
    }

    public async Task RecalculateForGroupAsync(AuthorizationGroup group, IDocumentSession session, CancellationToken ct = default)
    {
        if (group.MembershipMode != MembershipMode.Auto) return;

        if (string.IsNullOrWhiteSpace(group.CompiledMembershipScript))
        {
            if (group.MemberIds.Count > 0)
            {
                session.Events.Append(group.Id, new AuthorizationGroupMembershipRecomputedEvent(group.Id, []));
            }
            return;
        }

        try
        {
            var predicate = evaluator.BuildPredicate<PrincipalDirectory>(group.CompiledMembershipScript);

            // Single SQL query — translator-produced expression is byte-identical to a C# source lambda.
            // Self-membership is excluded for readability (a "(p) => true"-style script
            // would otherwise list the group as its own member). Circular chains via
            // *other* groups remain allowed — every graph traversal guards cycles with
            // a `visited` HashSet, so there's no infinite-loop risk.
            var groupId = group.Id;
            var newMembers = await session.Query<PrincipalDirectory>()
                .Where(p => !p.IsDeleted && p.Id != groupId)
                .Where(predicate)
                .Select(p => p.Id)
                .ToListAsync(ct);

            var changed = !SameSet(group.MemberIds, newMembers.ToList());
            var hadError = !string.IsNullOrEmpty(group.MembershipLastError);

            if (changed || hadError)
            {
                logger.LogInformation(
                    "[AutoMembership] Group {GroupId} ({GroupName}) recomputed: {Count} members",
                    group.Id, group.Name, newMembers.Count);

                // Emit even when the set is unchanged but we're recovering from a
                // previous failure — clears MembershipLastError in the projection.
                session.Events.Append(group.Id, new AuthorizationGroupMembershipRecomputedEvent(
                    group.Id,
                    changed ? newMembers.ToList() : group.MemberIds));
            }
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message is { Length: > 0 } inner
                ? $"{ex.Message} — {inner}"
                : ex.Message;

            // Idempotent guard — if the group already carries this exact error,
            // the sync path (UpdateGroupCommand) has already emitted the event.
            // The follow-up async handler would produce identical noise otherwise.
            if (group.MembershipLastError == message)
            {
                logger.LogDebug(
                    "[AutoMembership] Group {GroupId} already records this error, skipping duplicate event",
                    group.Id);
                return;
            }

            logger.LogWarning(ex,
                "[AutoMembership] Group {GroupId} ({GroupName}) recompute failed",
                group.Id, group.Name);

            session.Events.Append(group.Id,
                new AuthorizationGroupMembershipRecomputeFailedEvent(group.Id, message));
        }
    }

    public async Task RemoveUserFromAllAutoGroupsAsync(Guid principalId, IDocumentSession session, CancellationToken ct = default)
    {
        var groups = await session.Query<AuthorizationGroup>()
            .Where(g => !g.IsDeleted && g.MembershipMode == MembershipMode.Auto && g.MemberIds.Contains(principalId))
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            var newMembers = group.MemberIds.Where(id => id != principalId).ToList();
            session.Events.Append(group.Id, new AuthorizationGroupMembershipRecomputedEvent(group.Id, newMembers));
        }
    }

    /// <summary>
    /// Compiles the predicate and invokes it against the loaded principal. Guards against
    /// NullReferenceException (e.g. <c>p.Email.endsWith(...)</c> when Email is null)
    /// by treating exceptions as "not a member" — the safe default.
    /// </summary>
    private bool EvaluateSafe(string compiledScript, PrincipalDirectory principal)
    {
        try
        {
            var compiled = evaluator.BuildPredicate<PrincipalDirectory>(compiledScript).Compile();
            return compiled(principal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Membership predicate threw for principal {PrincipalId}", principal.Id);
            return false;
        }
    }

    private static bool SameSet(List<Guid> a, List<Guid> b)
        => a.Count == b.Count && a.ToHashSet().SetEquals(b);

    /// <summary>
    /// True if the two path-sets share a node (in either direction). Paths are
    /// dotted (e.g. <c>"PrincipalDirectory.Person.Firstname"</c>); a change to
    /// <c>"PrincipalDirectory.Person"</c> matches a script that depends on
    /// <c>"PrincipalDirectory.Person.Firstname"</c> and vice versa.
    /// </summary>
    private static bool DepsIntersect(IEnumerable<string> scriptDeps, IEnumerable<string> changed)
    {
        var changedSet = changed as ICollection<string> ?? changed.ToList();
        foreach (var dep in scriptDeps)
        {
            foreach (var c in changedSet)
            {
                if (dep == c) return true;
                if (dep.StartsWith(c + ".", StringComparison.Ordinal)) return true;
                if (c.StartsWith(dep + ".", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
}
