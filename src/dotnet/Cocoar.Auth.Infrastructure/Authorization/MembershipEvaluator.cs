using System.Linq.Expressions;
using Cocoar.Auth.Application.Authorization;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.Linq.Dependencies;
using Cocoar.JsEval.TypeScript;

namespace Cocoar.Auth.Infrastructure.Authorization;

public class MembershipEvaluator(
    JsEngine jsEngine,
    TsTranspiler tsTranspiler) : IMembershipEvaluator
{
    public string TranspileMembershipScript(string typeScript)
        => tsTranspiler.Transpile(typeScript);

    public Expression<Func<TPrincipalView, bool>> BuildPredicate<TPrincipalView>(string compiledScript)
    {
        // Let exceptions bubble up — the recalculator translates them into a
        // MembershipRecomputeFailedEvent so the UI can surface the error instead
        // of silently reporting "0 members".
        using var _ = JsLinqContext.Scope(jsEngine.UnderlyingEngine);
        var jsFn = jsEngine.EvaluateExpression(compiledScript);
        return JsExpressionTranslator.Translate<TPrincipalView, bool>(jsFn, jsEngine.UnderlyingEngine);
    }

    public IReadOnlyList<string>? CollectDependencies<TPrincipalView>(string compiledScript)
    {
        var predicate = BuildPredicate<TPrincipalView>(compiledScript);
        var deps = ExpressionDependencyCollector.Collect(predicate);
        // Unsafe means the collector hit dynamic/unanalyzable access — be defensive
        // and signal "invalidate-all" by returning null.
        if (deps.Unsafe) return null;

        // Store as FQN ("PrincipalDirectory.Person.Firstname") so the dependency set
        // is unambiguous if/when access-script dep-tracking is added on other view types.
        var prefix = typeof(TPrincipalView).Name + ".";
        return deps.Paths.Select(p => prefix + p).ToList();
    }
}
