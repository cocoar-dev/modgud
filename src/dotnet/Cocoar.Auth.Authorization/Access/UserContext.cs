namespace Cocoar.Auth.Authorization.Access;

/// <summary>
/// Context handed to access-policy scripts as the <c>user</c> variable. Provides
/// principal identity, permissions, and group membership (both names and ids).
/// <para>
/// <see cref="GroupIds"/> contains <b>transitive</b> memberships — direct groups
/// plus all ancestor groups via nested-group resolution. This lets scripts detect
/// cases like "user is a member of a group that's assigned as Responsible on this
/// resource" without re-traversing the graph in script code.
/// </para>
/// <para>
/// The library populates only these four fields. Apps that need to expose extra
/// data in scripts (tenant id, department, etc.) register an enricher — see
/// <c>IUserContextEnricher</c>.
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
        if (Permissions.Contains("app:admin"))
            return true;
        return Permissions.Contains(permission);
    }
}
