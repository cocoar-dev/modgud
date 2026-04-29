using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Authorization.Services;
using Marten;

namespace Cocoar.Auth.Authentication.Identity;

/// <summary>
/// Resolves the email recipients for admin-targeted notifications. The recipient set is
/// the union of email addresses produced by <see cref="IPrincipalEmailResolver"/> over
/// every <see cref="Group"/> whose roles effectively grant <c>app:admin</c>.
/// A group with its own shared mailbox is addressed directly; a group without one is
/// expanded to its members. The lookup matches <c>SetupEndpoints.AdminExistsAsync</c> so
/// we stay consistent with the „an admin exists" definition the rest of the app uses.
/// </summary>
public interface IAdminNotifier
{
    Task<IReadOnlyList<string>> GetAdminRecipientsAsync(CancellationToken ct = default);
}

public class AdminNotifier(IQuerySession session, IPrincipalEmailResolver resolver) : IAdminNotifier
{
    public async Task<IReadOnlyList<string>> GetAdminRecipientsAsync(CancellationToken ct = default)
    {
        // Roles that effectively grant "app:admin" — same matcher as SetupEndpoints.
        var adminRoles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted
                     && ((r.ResourceType == "app" && r.Permissions.Contains("admin"))
                         || r.Permissions.Contains("app:admin")))
            .ToListAsync(ct);

        if (adminRoles.Count == 0) return [];

        var adminRoleIds = adminRoles.Select(r => r.Id).ToArray();

        var adminGroups = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.RoleIds.Any(id => id.IsOneOf(adminRoleIds)))
            .ToListAsync(ct);

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in adminGroups)
        {
            var resolved = await resolver.ResolveEmailsAsync(g.Id, ct);
            foreach (var e in resolved) emails.Add(e);
        }
        return emails.ToList();
    }
}
