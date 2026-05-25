using System.Linq.Expressions;

namespace Modgud.Authorization.Membership;

/// <summary>
/// Transpiles membership-predicate scripts (TypeScript → JavaScript) and builds
/// the resulting arrow functions into LINQ <see cref="Expression{TDelegate}"/>
/// trees so they can be applied as Marten WHERE-clauses.
/// </summary>
public interface IMembershipEvaluator
{
    /// <summary>Transpiles a user-written TypeScript arrow-function predicate to JS.</summary>
    string TranspileMembershipScript(string typeScript);

    /// <summary>
    /// Compiles a JS arrow-function predicate into an <see cref="Expression{TDelegate}"/>
    /// tree. <typeparamref name="TPrincipal"/> is the app's concrete principal type
    /// (typically a <c>Person</c>-derived class) — the resulting expression can be
    /// used with <c>session.Query&lt;TPrincipal&gt;().Where(predicate)</c> or
    /// compiled for in-memory evaluation.
    /// <para>
    /// Closes Gap-1 / Gap-4 from the JsEval threat model: the call has a
    /// hard wall-clock budget and reacts to <paramref name="ct"/>.
    /// On timeout or cancellation, the underlying Jint engine is signalled
    /// to stop and a <see cref="OperationCanceledException"/> is thrown.
    /// </para>
    /// </summary>
    Expression<Func<TPrincipal, bool>> BuildPredicate<TPrincipal>(
        string compiledScript,
        CancellationToken ct = default);

    /// <summary>
    /// Inspects the script once at save-time and returns the dotted property
    /// paths it reads. Returns <c>null</c> when the collector flags any
    /// unanalyzable access — callers treat that as "invalidate-all".
    /// </summary>
    IReadOnlyList<string>? CollectDependencies<TPrincipal>(string compiledScript);
}
