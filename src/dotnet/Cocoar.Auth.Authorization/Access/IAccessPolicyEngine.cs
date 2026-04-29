using System.Linq.Expressions;

namespace Cocoar.Auth.Authorization.Access;

/// <summary>
/// Resolves access filters from group-assigned JavaScript access scripts and
/// turns them into <see cref="Expression{TDelegate}"/> trees usable with
/// Marten's LINQ provider.
/// <para>
/// Script convention: each <see cref="ResourceAccessScript"/> is an async function
/// that receives a query wrapper and calls <c>.where(t =&gt; ...)</c> on it, e.g.
/// <c>async function QueryTodos(todos, user) { return todos.where(t =&gt; t.Responsibles.some(r =&gt; r.Id === user.Id)); }</c>.
/// Scripts from all of the user's groups for the requested resource type are
/// OR-combined into a single predicate.
/// </para>
/// <para>
/// Filters are always app-scoped — only groups active in <paramref name="appSlug"/>
/// (per <c>Group.BoundTo</c>) and roles belonging to it contribute. Realm-wide
/// admins (groups with the <c>"*"</c> wildcard in BoundTo) bypass entirely via
/// the realm-admin permission.
/// </para>
/// <para>
/// Convenience wrappers (<c>CanAccessTodoAsync</c>, <c>CanCreateCustomerAsync</c>,
/// …) live in the consuming app — the library stops at the filter expression,
/// so it stays agnostic of each app's view types and id conventions.
/// </para>
/// </summary>
public interface IAccessPolicyEngine
{
    /// <summary>
    /// Builds the union of every access script attached to groups the user is a
    /// member of for the given <paramref name="resourceType"/> within
    /// <paramref name="appSlug"/>. Returns:
    /// <list type="bullet">
    ///   <item><c>null</c> — admin bypass or unrestricted script: no filter, grant all rows.</item>
    ///   <item><c>_ =&gt; false</c> — no scripts match; default deny.</item>
    ///   <item>A real predicate — the user sees rows matching the OR of every contributing script.</item>
    /// </list>
    /// </summary>
    Task<Expression<Func<TView, bool>>?> BuildFilterAsync<TView>(
        Guid userId, string appSlug, string resourceType,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="BuildFilterAsync{TView}"/> but only considers groups whose
    /// roles carry <paramref name="permission"/>. Prevents the "write without
    /// scope" cross-group leak where Group A grants read+scope and Group B
    /// grants write without scope. <paramref name="permission"/> is the fully
    /// qualified <c>app:resource:action</c> string.
    /// </summary>
    Task<Expression<Func<TView, bool>>?> BuildFilterForActionAsync<TView>(
        Guid userId, string appSlug, string resourceType, string permission,
        CancellationToken ct = default);

    /// <summary>Transpiles a TypeScript access script source to JavaScript.</summary>
    string TranspileTypeScript(string typeScript);
}
