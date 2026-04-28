using System.Linq.Expressions;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.Linq.Dependencies;
using Cocoar.JsEval.TypeScript;

namespace TimeToDo.Authorization.Access;

public class MembershipEvaluator(
    JsEngine jsEngine,
    TsTranspiler tsTranspiler) : IMembershipEvaluator
{
    public string TranspileMembershipScript(string typeScript)
        => tsTranspiler.Transpile(typeScript);

    public Expression<Func<TPrincipal, bool>> BuildPredicate<TPrincipal>(string compiledScript)
    {
        // Let exceptions bubble up — the caller (recalculator) translates them
        // into a MembershipRecomputeFailedEvent so the UI can surface the error
        // instead of silently reporting "0 members".
        var options = new TranslationOptions { DiscriminatorMappings = jsEngine.Options.DiscriminatorMappings };
        using var _ = JsLinqContext.Scope(jsEngine.UnderlyingEngine, options);
        var jsFn = jsEngine.EvaluateExpression(compiledScript);
        return JsExpressionTranslator.Translate<TPrincipal, bool>(jsFn, jsEngine.UnderlyingEngine, options);
    }

    public IReadOnlyList<string>? CollectDependencies<TPrincipal>(string compiledScript)
    {
        var predicate = BuildPredicate<TPrincipal>(compiledScript);
        var deps = ExpressionDependencyCollector.Collect(predicate);
        // Unsafe = collector hit dynamic/unanalyzable access — signal
        // "invalidate-all" by returning null.
        if (deps.Unsafe) return null;

        // Prefix with the type name so the dependency set is unambiguous once
        // scripts for multiple view types run through the same pipeline.
        var prefix = typeof(TPrincipal).Name + ".";
        return deps.Paths.Select(p => prefix + p).ToList();
    }
}
