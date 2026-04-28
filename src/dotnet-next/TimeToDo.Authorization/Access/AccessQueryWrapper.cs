using System.Linq.Expressions;
using Cocoar.JsEval.Linq;
using Jint.Native;

namespace TimeToDo.Authorization.Access;

/// <summary>
/// Immutable JS-callable wrapper passed to access policy scripts as the
/// <c>todos</c> / <c>customers</c> variable. Scripts call
/// <c>todos = todos.where(t =&gt; ...)</c> which returns a new instance with the
/// predicate appended — exactly like a real <see cref="IQueryable{T}"/>.
/// The script passes the final wrapper to <c>setResult(todos)</c>, which the
/// C# engine reads to extract the accumulated predicates.
/// </summary>
/// <remarks>
/// Multiple chained <c>.where()</c> calls are AND-combined (restriction).
/// Multiple group scripts are OR-combined by the calling engine.
/// </remarks>
public sealed class AccessQueryWrapper<TView>
{
    private readonly Jint.Engine _engine;
    private readonly Expression<Func<TView, bool>>[] _expressions;

    internal AccessQueryWrapper(Jint.Engine engine) : this(engine, []) { }

    private AccessQueryWrapper(Jint.Engine engine, Expression<Func<TView, bool>>[] exprs)
    {
        _engine = engine;
        _expressions = exprs;
    }

    /// <summary>
    /// Called from JS: <c>todos = todos.where(t =&gt; ...)</c>.
    /// Returns a new wrapper with the predicate appended — does not mutate this instance.
    /// </summary>
    public AccessQueryWrapper<TView> where(JsValue predicate)
    {
        var engine = JsLinqContext.CurrentEngine ?? _engine;
        var expr = JsExpressionTranslator.Translate<TView, bool>(predicate, engine);
        return new AccessQueryWrapper<TView>(_engine, [.._expressions, expr]);
    }

    internal IReadOnlyList<Expression<Func<TView, bool>>> Expressions => _expressions;
}
