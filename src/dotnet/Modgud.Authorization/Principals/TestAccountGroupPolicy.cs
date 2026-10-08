using ErrorOr;
using Marten;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Roles;

namespace Modgud.Authorization.Principals;

/// <summary>
/// ADR 0026 — which groups are closed to test accounts. A group is closed when it
/// excludes test accounts (<see cref="Group.ExcludeTestAccounts"/>) or when one of its
/// roles is part of Modgud's own administration: a realm-admin role, or a role of the
/// IdP's own Apps (<see cref="AppSlugs.Modgud"/>, <see cref="AppSlugs.ControlPlane"/>).
/// A test account is never an effective member of a closed group, and membership is cut
/// there: the groups above a closed group are not reached through it either.
/// </summary>
public static class TestAccountGroupPolicy
{
    /// <summary>The roles that make a group part of Modgud's own administration.</summary>
    public static async Task<HashSet<Guid>> AdministrationRoleIdsAsync(IQuerySession session, CancellationToken ct)
    {
        var adminAppIds = (await session.Query<App>()
                .Where(a => a.Slug == AppSlugs.Modgud || a.Slug == AppSlugs.ControlPlane)
                .ToListAsync(ct))
            .Select(a => a.Id)
            .ToHashSet();
        var roles = await session.Query<PermissionRole>().Where(r => !r.IsDeleted).ToListAsync(ct);
        return roles
            .Where(r => r.IsRealmAdmin || (r.AppId.HasValue && adminAppIds.Contains(r.AppId.Value)))
            .Select(r => r.Id)
            .ToHashSet();
    }

    public static bool IsClosed(Group group, IReadOnlySet<Guid> administrationRoleIds) =>
        IsClosed(group.ExcludeTestAccounts, group.RoleIds, administrationRoleIds);

    public static bool IsClosed(bool excludeTestAccounts, IEnumerable<Guid> roleIds, IReadOnlySet<Guid> administrationRoleIds) =>
        excludeTestAccounts || roleIds.Any(administrationRoleIds.Contains);

    /// <summary>Whether the principal is a person marked as a test account.</summary>
    public static async Task<bool> IsTestAccountAsync(IQuerySession session, Guid principalId, CancellationToken ct) =>
        (await session.LoadAsync<Person>(principalId, ct))?.IsTestAccount == true;

    /// <summary>The test accounts among <paramref name="memberIds"/> (direct members).</summary>
    public static async Task<List<Person>> TestAccountsAmongAsync(
        IQuerySession session, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        if (memberIds.Count == 0) return [];
        var ids = memberIds.ToArray();
        return (await session.Query<Person>().Where(p => p.Id.IsOneOf(ids) && !p.IsDeleted).ToListAsync(ct))
            .Where(p => p.IsTestAccount)
            .ToList();
    }

    /// <summary>
    /// Write-time guard for a group's direct members: a closed group refuses test
    /// accounts, naming them. Returns <c>null</c> when nothing conflicts.
    /// </summary>
    public static async Task<Error?> RejectTestAccountMembersAsync(
        IQuerySession session,
        bool excludeTestAccounts,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken ct)
    {
        var testAccounts = await TestAccountsAmongAsync(session, memberIds, ct);
        if (testAccounts.Count == 0) return null;
        var administrationRoleIds = await AdministrationRoleIdsAsync(session, ct);
        if (!IsClosed(excludeTestAccounts, roleIds, administrationRoleIds)) return null;

        var names = string.Join(", ", testAccounts.Select(p => p.AccountName ?? p.Email ?? p.Id.ToString()));
        return excludeTestAccounts
            ? Error.Validation("Group.TestAccountExcluded",
                $"This group excludes test accounts. Remove them first: {names}.")
            : Error.Validation("Group.TestAccountAdministration",
                $"This group grants Modgud's own administration, which a test account never holds. Remove them first: {names}.");
    }
}
