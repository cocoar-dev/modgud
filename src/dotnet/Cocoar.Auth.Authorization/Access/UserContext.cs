using Cocoar.Auth.Authorization.Services;

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

    /// <summary>
    /// Returns true iff the principal holds <paramref name="permission"/>. Uses
    /// the same evaluation rules as the backend permission service:
    /// <list type="bullet">
    ///   <item><c>app:admin</c> in grants → true for any permission</item>
    ///   <item>Exact match on <paramref name="permission"/></item>
    ///   <item><c>&lt;resource&gt;:admin</c> grants every action on that resource
    ///         (e.g. <c>oauth-client:admin</c> covers <c>oauth-client:read</c>)</item>
    /// </list>
    /// Scripts and backend filters now agree on the answer for the same principal.
    /// </summary>
    public bool HasPermission(string permission) =>
        PermissionEvaluator.Evaluate(Permissions, permission);
}
