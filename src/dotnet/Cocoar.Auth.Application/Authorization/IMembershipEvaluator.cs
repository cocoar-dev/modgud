using System.Linq.Expressions;

namespace Cocoar.Auth.Application.Authorization;

/// <summary>
/// Compiles and analyzes auto-membership predicates that decide which principals
/// belong to an <c>AuthorizationGroup</c>. The predicate operates against the
/// inline <c>PrincipalDirectory</c> projection so changes are visible immediately
/// after a commit.
/// </summary>
public interface IMembershipEvaluator
{
    /// <summary>Transpiles a user-written TypeScript arrow-function predicate to JS.</summary>
    string TranspileMembershipScript(string typeScript);

    /// <summary>
    /// Compiles a JS arrow-function predicate into an <see cref="Expression{TDelegate}"/>
    /// tree that can be applied to a Marten <c>IQueryable</c> over the principal
    /// directory or compiled to a delegate for in-memory evaluation. The TView type
    /// is the principal directory projection in Infrastructure.
    /// </summary>
    Expression<Func<TPrincipalView, bool>> BuildPredicate<TPrincipalView>(string compiledScript);

    /// <summary>
    /// Inspects the script once at save-time and returns the dotted property paths
    /// it reads from the principal view. Returns <c>null</c> when the collector flags
    /// any unanalyzable access ("invalidate-all") so callers know they must always
    /// re-run the script on principal-side changes.
    /// </summary>
    IReadOnlyList<string>? CollectDependencies<TPrincipalView>(string compiledScript);
}
