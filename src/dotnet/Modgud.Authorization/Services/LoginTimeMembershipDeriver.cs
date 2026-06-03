using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Marten;
using Microsoft.Extensions.Logging;

namespace Modgud.Authorization.Services;

/// <summary>
/// Federation v1 (decision C) — computes a login's ephemeral, session-scoped
/// group membership from the current provider's claims, WITHOUT touching durable
/// <c>Group.MemberIds</c>. Read-only by construction (an <see cref="IQuerySession"/>,
/// no <c>session.Events</c> / <c>SaveChanges</c>): it can never persist membership
/// and never appends <c>GroupMembershipRecomputedEvent</c>.
/// <para>
/// Modeled on <see cref="EffectiveGroupsResolver"/>, but it evaluates ONLY
/// <c>MembershipMode==Auto</c> + <c>Group.ExternallyDrivable</c> groups, binds the
/// predicate to an in-memory <see cref="EvalPrincipal"/> (local Person fields ∪ the
/// current provider's groups), and reuses the shared
/// <see cref="MembershipPredicateEvaluation.EvaluateSafe"/> so it agrees with the
/// durable engine on null/case/collation.
/// </para>
/// </summary>
public interface ILoginTimeMembershipDeriver
{
    /// <summary>
    /// Returns the group IDs the user matches this login via externally-drivable
    /// scripts. <paramref name="externalGroups"/> is the current provider's
    /// <c>groups</c> claim (already trust-gated by the caller); <paramref name="source"/>
    /// is the <c>"provider:&lt;slug&gt;"</c> tag. realm:admin-conferring groups are
    /// defensively excluded (the config guard should already make that impossible).
    /// </summary>
    Task<DerivedMembershipResult> DeriveAsync(
        Guid principalId,
        IReadOnlyList<string> externalGroups,
        string source,
        CancellationToken ct = default);
}

public sealed record DerivedMembershipResult(
    IReadOnlyList<Guid> MatchedGroupIds,
    int DroppedRealmAdminCount = 0)
{
    public static readonly DerivedMembershipResult Empty = new([]);
}

public sealed class LoginTimeMembershipDeriver(
    IQuerySession session,
    IMembershipEvaluator evaluator,
    ILogger<LoginTimeMembershipDeriver> logger) : ILoginTimeMembershipDeriver
{
    public async Task<DerivedMembershipResult> DeriveAsync(
        Guid principalId,
        IReadOnlyList<string> externalGroups,
        string source,
        CancellationToken ct = default)
    {
        var person = await session.LoadAsync<Person>(principalId, ct);
        if (person is null || person.IsDeleted || !person.IsActive)
            return DerivedMembershipResult.Empty;

        // Hydrate the in-memory eval principal IDENTICALLY to the persisted Person
        // (same null/case/collation) and overlay the current provider's groups.
        var eval = new EvalPrincipal
        {
            Id = person.Id,
            IsActive = person.IsActive,
            IsDeleted = person.IsDeleted,
            AccountName = person.AccountName,
            Firstname = person.Firstname,
            Lastname = person.Lastname,
            Acronym = person.Acronym,
            Email = person.Email,
            NormalizedUserName = person.NormalizedUserName,
            NormalizedEmail = person.NormalizedEmail,
            ExternalIdentities = person.ExternalIdentities,
            ExternalGroups = [.. externalGroups],
            Source = source,
        };

        var drivable = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.MembershipMode == MembershipMode.Auto && g.ExternallyDrivable)
            .ToListAsync(ct);

        var matched = new List<Group>();
        foreach (var g in drivable)
        {
            if (string.IsNullOrWhiteSpace(g.CompiledMembershipScript)) continue;
            if (g.Id == principalId) continue; // a group never lists itself
            if (MembershipPredicateEvaluation.EvaluateSafe(
                    evaluator, g.CompiledMembershipScript!, eval, logger, principalId, ct))
                matched.Add(g);
        }

        if (matched.Count == 0) return DerivedMembershipResult.Empty;

        // Belt-and-braces: realm:admin is hard local-only (decision G). The
        // write-time config guard should already forbid an ExternallyDrivable
        // group from conferring realm:admin — defensively drop any that slipped
        // through (e.g. a role flipped IsRealmAdmin after the group was marked).
        var (safe, dropped) = await StripRealmAdminConferringAsync(matched, ct);
        return new DerivedMembershipResult([.. safe.Select(g => g.Id)], dropped);
    }

    // Returns the surviving groups plus the count of realm:admin-conferring groups
    // that were defensively dropped. The caller (which can reach the audit store —
    // this Authorization layer cannot) turns a non-zero count into a
    // security.privilege_escalation_blocked audit record; here it stays a plain
    // diagnostic log.
    private async Task<(IReadOnlyList<Group> Safe, int Dropped)> StripRealmAdminConferringAsync(
        List<Group> groups, CancellationToken ct)
    {
        var roleIds = groups.SelectMany(g => g.RoleIds).Distinct().ToList();
        if (roleIds.Count == 0) return (groups, 0);

        var realmAdminRoleIds = (await session.Query<PermissionRole>()
                .Where(r => roleIds.Contains(r.Id) && r.IsRealmAdmin)
                .ToListAsync(ct))
            .Select(r => r.Id)
            .ToHashSet();
        if (realmAdminRoleIds.Count == 0) return (groups, 0);

        var safe = groups.Where(g => !g.RoleIds.Any(realmAdminRoleIds.Contains)).ToList();
        var dropped = groups.Count - safe.Count;
        if (dropped > 0)
            logger.LogWarning(
                "dropped {Count} externally-derived group(s) conferring realm:admin (config guard should have prevented this)",
                dropped);
        return (safe, dropped);
    }
}
