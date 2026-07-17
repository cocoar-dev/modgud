using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Authorization.Services;
using Marten;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Resolves the email recipients for admin-targeted notifications. The recipient set is
/// the union of email addresses produced by <see cref="IPrincipalEmailResolver"/> over
/// every <see cref="Group"/> whose roles carry the realm-admin flag. A group with its
/// own shared mailbox is addressed directly; a group without one is expanded to its
/// members. The lookup matches the role+group shape produced by
/// <c>RealmAdminBootstrapper</c> (System Admin role + Administrators group).
/// </summary>
public interface IAdminNotifier
{
    /// <summary>
    /// Resolves admin email recipients (shared mailbox if the group has one,
    /// otherwise expanded per-member). Used by email-channel notifications.
    /// </summary>
    Task<IReadOnlyList<string>> GetAdminRecipientsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves admin recipients as user-IDs (always per-member, never the
    /// group's shared mailbox — in-app notifications go to a real user inbox).
    /// Used by inbox-channel notifications.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAdminRecipientUserIdsAsync(CancellationToken ct = default);
}

public class AdminNotifier(IQuerySession session, IPrincipalEmailResolver resolver) : IAdminNotifier
{
    public async Task<IReadOnlyList<string>> GetAdminRecipientsAsync(CancellationToken ct = default)
    {
        var adminGroups = await LoadAdminGroupsAsync(ct);
        if (adminGroups.Count == 0) return [];

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in adminGroups)
        {
            var resolved = await resolver.ResolveEmailsAsync(g.Id, ct);
            foreach (var e in resolved) emails.Add(e);
        }
        return emails.ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetAdminRecipientUserIdsAsync(CancellationToken ct = default)
    {
        var adminGroups = await LoadAdminGroupsAsync(ct);
        if (adminGroups.Count == 0) return [];

        var userIds = new HashSet<Guid>();
        foreach (var g in adminGroups)
            foreach (var memberId in g.MemberIds)
                userIds.Add(memberId);
        return userIds.ToList();
    }

    private async Task<IReadOnlyList<Group>> LoadAdminGroupsAsync(CancellationToken ct)
    {
        // Roles that grant realm:admin — same matcher as RealmAdminBootstrapper
        // uses to detect a pre-existing admin role at re-bootstrap time.
        var adminRoles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.IsRealmAdmin)
            .ToListAsync(ct);

        if (adminRoles.Count == 0) return [];

        var adminRoleIds = adminRoles.Select(r => r.Id).ToArray();

        return await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.RoleIds.Any(id => id.IsOneOf(adminRoleIds)))
            .ToListAsync(ct);
    }
}
