using Cocoar.Auth.Domain.Authorization;

namespace Cocoar.Auth.Application.Authorization;

/// <summary>
/// Context available to access policy scripts as the 'user' variable.
/// Provides principal identity, permissions, and group membership (both names and IDs)
/// for script logic.
/// <para>
/// <see cref="GroupIds"/> includes *transitive* group memberships — direct groups plus
/// all ancestor groups via nested-group resolution. This lets scripts detect cases
/// like "user is a member of a group that's referenced by some other resource".
/// </para>
/// </summary>
public class UserContext
{
    public Guid Id { get; init; }
    public List<string> Permissions { get; init; } = [];
    public List<string> Groups { get; init; } = [];
    public List<Guid> GroupIds { get; init; } = [];

    public bool HasPermission(string permission)
    {
        if (Permissions.Contains(Domain.Authorization.Permissions.SystemAdmin) ||
            Permissions.Contains(Domain.Authorization.Permissions.TenantAdmin))
            return true;
        return Permissions.Contains(permission);
    }
}
