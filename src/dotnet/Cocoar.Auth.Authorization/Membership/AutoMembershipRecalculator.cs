using Marten;
using Microsoft.Extensions.Logging;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;

namespace Cocoar.Auth.Authorization.Membership;

public interface IAutoMembershipRecalculator
{
    Task RecalculateForPrincipalAsync(
        Guid principalId,
        IDocumentSession session,
        IReadOnlyCollection<string>? changedPaths = null,
        CancellationToken ct = default);

    Task RecalculateForGroupAsync(Group group, IDocumentSession session, CancellationToken ct = default);

    Task RemoveUserFromAllAutoGroupsAsync(Guid principalId, IDocumentSession session, CancellationToken ct = default);
}

/// <summary>
/// Recomputes the membership of auto-managed groups.
/// <list type="bullet">
///   <item>For a single group: runs the predicate as one SQL query against the
///         <see cref="Person"/> document (Marten translates the JsEval-emitted
///         expression to a JSONB filter).</item>
///   <item>For a single principal: compiles the predicate in-memory and tests
///         the loaded principal.</item>
/// </list>
/// Appends <see cref="GroupMembershipRecomputedEvent"/> when the resulting
/// MemberIds differ from the current state. The group projection is inline,
/// so changes are visible immediately after
/// <see cref="IDocumentSession.SaveChangesAsync"/>.
/// </summary>
public class AutoMembershipRecalculator(
    IMembershipEvaluator evaluator,
    ILogger<AutoMembershipRecalculator> logger) : IAutoMembershipRecalculator
{
    public async Task RecalculateForPrincipalAsync(
        Guid principalId,
        IDocumentSession session,
        IReadOnlyCollection<string>? changedPaths = null,
        CancellationToken ct = default)
    {
        var principal = await session.LoadAsync<Principal>(principalId, ct);

        var groups = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.MembershipMode == MembershipMode.Auto)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.CompiledMembershipScript)) continue;

            // A group never lists itself (matches the filter in RecalculateForGroupAsync).
            if (group.Id == principalId) continue;

            // Dependency-driven skip: if the caller names which principal paths
            // changed and the script's recorded dependencies don't intersect
            // them, the result can't change. Null deps = "invalidate-all".
            if (changedPaths is { Count: > 0 } &&
                group.MembershipScriptDependencies is { Count: > 0 } deps &&
                !DepsIntersect(deps, changedPaths))
            {
                continue;
            }

            var shouldBeMember = principal is not null
                && !principal.IsDeleted
                && EvaluateSafe(group.CompiledMembershipScript, principal, ct);
            var isMember = group.MemberIds.Contains(principalId);

            if (shouldBeMember == isMember) continue;

            var newMembers = shouldBeMember
                ? [.. group.MemberIds, principalId]
                : group.MemberIds.Where(id => id != principalId).ToList();

            logger.LogInformation(
                "[AutoMembership] Principal {PrincipalId} {Action} group {GroupId} ({GroupName})",
                principalId, shouldBeMember ? "added to" : "removed from", group.Id, group.Name);

            session.Events.Append(group.Id, new GroupMembershipRecomputedEvent(group.Id, newMembers));
        }
    }

    public async Task RecalculateForGroupAsync(Group group, IDocumentSession session, CancellationToken ct = default)
    {
        if (group.MembershipMode != MembershipMode.Auto) return;

        if (string.IsNullOrWhiteSpace(group.CompiledMembershipScript))
        {
            if (group.MemberIds.Count > 0)
                session.Events.Append(group.Id, new GroupMembershipRecomputedEvent(group.Id, []));
            return;
        }

        try
        {
            var predicate = evaluator.BuildPredicate<Principal>(group.CompiledMembershipScript, ct);

            // One SQL query against mt_doc_principal (all sub-classes).
            // Self-membership is excluded so a `(p) => true` script doesn't list the
            // group as its own member.
            var groupId = group.Id;
            var newMembers = await session.Query<Principal>()
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
                session.Events.Append(group.Id, new GroupMembershipRecomputedEvent(
                    group.Id,
                    changed ? newMembers.ToList() : group.MemberIds));
            }
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message is { Length: > 0 } inner
                ? $"{ex.Message} — {inner}"
                : ex.Message;

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
                new GroupMembershipRecomputeFailedEvent(group.Id, message));
        }
    }

    public async Task RemoveUserFromAllAutoGroupsAsync(Guid principalId, IDocumentSession session, CancellationToken ct = default)
    {
        var groups = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.MembershipMode == MembershipMode.Auto && g.MemberIds.Contains(principalId))
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            var newMembers = group.MemberIds.Where(id => id != principalId).ToList();
            session.Events.Append(group.Id, new GroupMembershipRecomputedEvent(group.Id, newMembers));
        }
    }

    private bool EvaluateSafe(string compiledScript, Principal principal, CancellationToken ct)
    {
        try
        {
            var compiled = evaluator.BuildPredicate<Principal>(compiledScript, ct).Compile();
            return compiled(principal);
        }
        catch (Exception ex)
        {
            // NullReferenceException (e.g. `p.Email.endsWith(...)` when Email is null)
            // treated as "not a member" — the safe default.
            logger.LogWarning(ex, "Membership predicate threw for principal {PrincipalId}", principal.Id);
            return false;
        }
    }

    private static bool SameSet(List<Guid> a, List<Guid> b)
        => a.Count == b.Count && a.ToHashSet().SetEquals(b);

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
