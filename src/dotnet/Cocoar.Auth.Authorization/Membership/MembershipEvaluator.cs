using System.Linq.Expressions;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.Linq.Dependencies;
using Cocoar.JsEval.TypeScript;

namespace Cocoar.Auth.Authorization.Membership;

public class MembershipEvaluator(
    JsEngine jsEngine,
    TsTranspiler tsTranspiler) : IMembershipEvaluator
{
    /// <summary>
    /// Wall-clock budget for a single <see cref="BuildPredicate"/> call.
    /// Closes Gap-1 from the JsEval threat model — without this, a script
    /// containing a top-level `while(true){}` would hang the recompute
    /// thread until ASP.NET Core's request timeout.
    /// </summary>
    private static readonly TimeSpan EvaluationTimeBudget = TimeSpan.FromSeconds(2);

    public string TranspileMembershipScript(string typeScript)
        => tsTranspiler.Transpile(typeScript);

    public Expression<Func<TPrincipal, bool>> BuildPredicate<TPrincipal>(
        string compiledScript,
        CancellationToken ct = default)
    {
        // Run the engine + translator under a wall-clock budget AND honour
        // any caller-supplied CancellationToken. On either, signal the Jint
        // engine to stop (cooperative cancel via its own CTS) and surface
        // OperationCanceledException to the caller.
        // Let other exceptions bubble up — the caller (recalculator) maps
        // them to a MembershipRecomputeFailedEvent so the UI can surface
        // the error instead of silently reporting "0 members".
        using var timeoutCts = new CancellationTokenSource(EvaluationTimeBudget);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var _stopOnCancel = linkedCts.Token.Register(jsEngine.Stop);

        var options = new TranslationOptions { DiscriminatorMappings = jsEngine.Options.DiscriminatorMappings };
        using var _ = JsLinqContext.Scope(jsEngine.UnderlyingEngine, options);

        try
        {
            var jsFn = jsEngine.EvaluateExpression(compiledScript);
            return JsExpressionTranslator.Translate<TPrincipal, bool>(jsFn, jsEngine.UnderlyingEngine, options);
        }
        catch (Exception) when (linkedCts.IsCancellationRequested)
        {
            // Engine.Stop() was triggered by timeout or external cancel.
            // Whatever Jint surfaced (ExecutionCanceledException, ScriptException,
            // or some inner exception from the cooperative abort), normalise
            // into the standard cancel signal.
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                $"Membership-script evaluation exceeded the {EvaluationTimeBudget.TotalSeconds:F0}s budget. " +
                $"This usually points at an infinite loop or a quadratic allocation in the top-level script body.");
        }
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
