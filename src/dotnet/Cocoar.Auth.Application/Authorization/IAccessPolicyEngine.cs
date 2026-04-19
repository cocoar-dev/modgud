using System.Linq.Expressions;

namespace Cocoar.Auth.Application.Authorization;

/// <summary>
/// Resolves access filters from group-assigned JavaScript arrow-function predicates
/// via <c>Cocoar.JsEval.Linq</c>, producing real <see cref="Expression{TDelegate}"/>
/// trees that Marten translates to SQL.
/// <para>
/// Script convention: each access script is a single arrow function expression
/// returning <c>bool</c>, e.g. <c>(t) =&gt; t.OwnerId === user.Id</c>. Multiple scripts
/// (from multiple groups) are OR-combined into a single predicate. A <c>null</c>
/// return value means "no filter, grant all" (admin bypass / unrestricted RBAC).
/// </para>
/// </summary>
public interface IAccessPolicyEngine
{
    /// <summary>
    /// Build a row-level access filter for resource type <paramref name="resourceType"/>
    /// applied to view type <typeparamref name="TView"/>.
    /// <list type="bullet">
    ///   <item><c>null</c> → no filter, unrestricted (admin or RBAC-style role).</item>
    ///   <item><c>_ =&gt; false</c> → default deny (no group grants this resource type).</item>
    ///   <item>otherwise → OR-combined predicate from all applicable groups' access scripts.</item>
    /// </list>
    /// </summary>
    Task<Expression<Func<TView, bool>>?> BuildFilterAsync<TView>(Guid userId, string resourceType, CancellationToken ct = default);

    /// <summary>Transpiles a TypeScript arrow-function predicate source to JavaScript.</summary>
    string TranspileTypeScript(string typeScript);
}
