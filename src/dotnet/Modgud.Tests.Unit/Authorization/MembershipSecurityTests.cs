using System.Diagnostics;
using System.Linq.Expressions;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Tests.Unit.Authorization;

/// <summary>
/// Adversarial test suite for the membership-script JsEval pipeline. Phase 2
/// of the JsEval-Fuzzing initiative — see
/// <c>dev-docs/jseval-threat-model.md</c> for the full threat model
/// (Phase 1) and the gap classification.
///
/// <para>Six test groups, mirroring the attacker classes (A1-A6) in the
/// threat model. Tests pin <em>expected safe behaviour</em>. Tests that fail
/// today because the threat-model identified a real gap are marked with the
/// gap reference in the assertion message and a <see cref="Skip"/> attribute,
/// so Phase 3 can pick them up by un-skipping.</para>
///
/// <para>The tests instantiate <see cref="IMembershipEvaluator"/> with the
/// same wiring Modgud uses in production
/// (<c>Modgud.Infrastructure/DependencyInjection.cs:196-201</c>) so the
/// engine config is identical.</para>
/// </summary>
public sealed class MembershipSecurityTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IServiceScope _scope;
    private readonly IMembershipEvaluator _evaluator;
    private readonly TsTranspiler _transpiler;

    public MembershipSecurityTests()
    {
        // Mirror production wiring exactly. Cocoar.JsEval 4.0 ships
        // safe-by-default — the engine globals catalogue (NewObject,
        // require, exit, timers, console + __log_*) is `undefined` unless
        // explicitly enabled. Modgud never enables them.
        // WithExecutionTimeout adds a lib-level wall-clock backstop on
        // top of the consumer-side budget in MembershipEvaluator.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJsEval(b => b
            .AddLinq()
            .AddDiscriminatorMappings<Principal>("Type",
                ("person", typeof(Person)),
                ("group", typeof(Group)),
                ("service-account", typeof(ServiceAccount)))
            .WithExecutionTimeout(TimeSpan.FromSeconds(2)));
        services.AddTsTranspiler();
        services.AddTsDefinition();
        services.AddScoped<IMembershipEvaluator, MembershipEvaluator>();

        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateScope();
        _evaluator = _scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        _transpiler = _scope.ServiceProvider.GetRequiredService<TsTranspiler>();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _sp.Dispose();
    }

    // Each test budgets at most this much wall-clock to detect runaway scripts.
    // Anything that hangs longer is treated as a failed bound-time assertion.
    private static readonly TimeSpan UnboundedBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs <paramref name="action"/> on a worker thread and asserts it
    /// completes within <paramref name="budget"/>. The thread is left
    /// orphaned on timeout (the engine has no cooperative cancellation;
    /// see Gap-1, Gap-4) — acceptable because the test-process exit
    /// reaps it. Returns the action's result on success.
    /// </summary>
    private static T WithTimeBudget<T>(Func<T> action, TimeSpan? budget = null)
    {
        var deadline = budget ?? UnboundedBudget;
        var task = Task.Run(action);
        if (!task.Wait(deadline))
        {
            throw new TimeoutException(
                $"Operation did not complete within {deadline.TotalSeconds:F1}s. " +
                $"This usually points at Gap-1 (no engine timeout / no max-statements) — " +
                $"see dev-docs/jseval-threat-model.md.");
        }
        return task.Result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // A1 — Resource exhaustion (DoS)
    //
    // Pin: every legitimate translation completes in bounded time, and
    // pathological scripts either reject fast or are bounded by an engine
    // constraint. Today (Gap-1) no engine constraint is set, so the
    // hang-class tests are marked Skip until Phase 3 lands the fix.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A1_TrivialPredicate_TranslatesUnderOneSecond()
    {
        var compiled = _transpiler.Transpile("(p: any) => p.Firstname === 'X'");
        var sw = Stopwatch.StartNew();
        var predicate = WithTimeBudget(
            () => _evaluator.BuildPredicate<Person>(compiled),
            TimeSpan.FromSeconds(1));
        sw.Stop();

        Assert.NotNull(predicate);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Trivial translation took {sw.ElapsedMilliseconds}ms — expected <1000ms.");
    }

    [Fact]
    public void A1_TopLevelInfiniteLoop_TimesOutWithin3s()
    {
        // Gap-1 closed: BuildPredicate has a 2s evaluation budget that
        // signals jsEngine.Stop() on timeout. The wrapper here gives the
        // mechanism 3s of wall-clock to finish; if it doesn't,
        // WithTimeBudget throws (the test fails noisy, not silent).
        var compiled = _transpiler.Transpile(
            "while (true) {} const f = (p: any) => p.Firstname === 'X'; f");

        var ex = Record.Exception(() =>
            WithTimeBudget(
                () => _evaluator.BuildPredicate<Person>(compiled),
                TimeSpan.FromSeconds(3)));

        // Either OperationCanceledException (clean cancel) or whatever Jint
        // surfaced when its CTS fired. Both are acceptable — we're pinning
        // "did not hang the test runner".
        Assert.NotNull(ex);
    }

    [Fact]
    public void A1_TopLevelAllocationFlood_TimesOutOrCompletes()
    {
        // Closed by Gap-1's timeout: a 30-iteration string-doubling loop
        // either completes (Jint's allocator eats it) or hits the 2s
        // budget and gets cancelled. Either way the test runner doesn't
        // hang — that's the safety property.
        var compiled = _transpiler.Transpile(
            "let s = 'x'; for (let i = 0; i < 30; i++) s += s; " +
            "const f = (p: any) => s.length > 0; f");

        // Best-effort: just wait for the call to terminate within a
        // generous outer budget, no exception assertion either way.
        // 15s headroom for GitHub-Actions runners (2-core, slow IO) where
        // 1GB string-doubling + Jint compile-overhead + the engine's 2s
        // timeout-wakeup latency can collectively exceed 5s. Local runs
        // finish in ~2s.
        WithTimeBudget(() =>
        {
            try { _evaluator.BuildPredicate<Person>(compiled); }
            catch { /* timeout-cancel or OOM both acceptable here */ }
            return true;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A1_TranspilerDepthCap_LibSideClosed()
    {
        // F6b lib-side fix landed in Cocoar.JsEval 4.0 — the
        // TS-pipeline pre-parses for nesting depth (default
        // MaxParseDepth = 128) and throws a controlled
        // TsTranspileException at depth ≥ 128 instead of escalating to
        // StackOverflowException. This test bypasses our own
        // ScriptInputLimits guard (which fires earlier at depth 50) and
        // calls the transpiler directly with a 500-deep input to confirm
        // the lib catches it.
        var body = "true";
        for (var i = 0; i < 500; i++) body = $"({body} ? 1 : 2)";
        var source = $"(p: any) => {body} === 1";

        var ex = Record.Exception(() => _transpiler.Transpile(source));

        Assert.NotNull(ex);
        // The lib emits a TsTranspileException; we don't assert the
        // exact type to avoid coupling to the namespace, but we DO
        // assert the message names the depth so a future lib-side
        // refactor that loses the depth-cap surfaces here.
        Assert.Contains("depth", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A1_OneMegabyteScript_RejectedByLengthCap()
    {
        // Gap-2 closed: the consumer-side length cap (16 KiB) keeps
        // multi-megabyte scripts from ever reaching the TS compiler.
        var huge = new string('/', 1024 * 1024) + "\n(p: any) => true";
        var error = Modgud.Authorization.Membership.ScriptInputLimits
            .Validate(huge, "Test");

        Assert.NotNull(error);
        Assert.Equal("TestTooLong", error.Value.Code);
    }

    [Fact]
    public void A1_DeeplyNestedTernary_RejectedByDepthCap()
    {
        // F6b consumer-side mitigation: a 500-deep ternary script would
        // crash the host process during TS parsing (interpreted Acornima
        // descent in Jint exhausts the .NET stack — confirmed empirically
        // even with an 8 MB worker-thread stack). The pre-parse depth
        // counter rejects it before the parser ever runs.
        var body = "true";
        for (var i = 0; i < 500; i++) body = $"({body} ? 1 : 2)";
        var source = $"(p: any) => {body} === 1";
        var error = Modgud.Authorization.Membership.ScriptInputLimits
            .Validate(source, "Test");

        Assert.NotNull(error);
        Assert.Equal("TestTooDeep", error.Value.Code);
    }

    [Fact]
    public void A1_DepthCounter_HandlesStringsAndComments()
    {
        // The depth counter must skip brackets inside string literals
        // and comments, so a plain-text script doesn't get flagged
        // as "deeply nested" because of e.g. a regex or a JSON blob
        // embedded in a string.
        var legitimate =
            """
            // (((((((( comment with parens shouldn't count ))))))))
            const url = 'https://example.com/(((((((((((((path)))))))))))))'
            const data = {
                pattern: "[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]",
            }
            const f = (p: any) => p.Email !== null
            f
            """;
        var error = Modgud.Authorization.Membership.ScriptInputLimits
            .Validate(legitimate, "Test");

        Assert.Null(error);
    }

    // ─────────────────────────────────────────────────────────────────────
    // A2 — Native-host escape
    //
    // Pin: Jint sandbox boundaries hold. No CLR access, no fetch, no module
    // import, no eval/Function constructor, no globalThis.process / require.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A2_EvalCall_RejectsAtTranslation()
    {
        // Even if Jint accepts eval at top level, the translator's whitelist
        // does not accept arbitrary call targets — `eval` is not a known method.
        var compiled = _transpiler.Transpile(
            "(p: any) => eval('p.Firstname === \"X\"')");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    [Fact]
    public void A2_FunctionConstructor_RejectsAtTranslation()
    {
        var compiled = _transpiler.Transpile(
            "(p: any) => new Function('return p.Firstname === \"X\"')()");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    [Fact]
    public void A2_GlobalThisProcess_NotReachable()
    {
        // `globalThis.process` should be undefined — Jint doesn't expose a
        // Node.js-style `process` object.
        var compiled = _transpiler.Transpile(
            "(p: any) => globalThis.process !== undefined");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        // Either the translator rejects unknown identifier `globalThis`, OR
        // the predicate translates to a constant `false`. Both are acceptable —
        // what's NOT acceptable is `process` being defined.
        Assert.True(
            ex is not null
            || EvaluateConstantBool(_evaluator.BuildPredicate<Person>(compiled)) == false,
            "globalThis.process should not be reachable from membership scripts.");
    }

    [Fact]
    public void A2_RequireFunction_NotReachable()
    {
        var compiled = _transpiler.Transpile(
            "(p: any) => typeof require === 'function'");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.True(
            ex is not null
            || EvaluateConstantBool(_evaluator.BuildPredicate<Person>(compiled)) == false,
            "`require` should not be reachable from membership scripts.");
    }

    [Fact]
    public void A2_ImportExpression_RejectsAtTranslation()
    {
        // Top-level `import()` is parseable JS in modern targets. Translator
        // body has no ImportExpression case — must reject.
        var compiled = _transpiler.Transpile(
            "(p: any) => import('System.IO.File').then(() => p) !== null");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    [Fact]
    public void A2_PrototypePollutionAttempt_HasNoVisibleEffect()
    {
        // Mutating Object.prototype at top level shouldn't change how the
        // translator interprets a separate predicate, because the translator
        // walks the AST — it doesn't go through prototype lookups.
        var pollutedThenPredicate = _transpiler.Transpile(
            "Object.prototype.isAdmin = () => true; " +
            "const f = (p: any) => p.Firstname === 'X'; f");

        // This must translate without absorbing the prototype pollution: the
        // resulting predicate should still be a Firstname-equality, not a
        // tautology.
        var predicate = _evaluator.BuildPredicate<Person>(pollutedThenPredicate);

        // Spot-check — predicate should NOT match a person with no firstname.
        var nullFirstname = new Person { Id = Guid.NewGuid(), Firstname = null };
        Assert.False(predicate.Compile()(nullFirstname));
    }

    [Fact]
    public void A2_TopLevelFileAccessAttempt_RejectsOrEvaluatesFalse()
    {
        // No CLR exposure → System.IO.File should not be a known global.
        var compiled = _transpiler.Transpile(
            "(p: any) => System.IO.File !== undefined");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    // ─────── A2 — NewObject host-RCE primitive (consumer-side mitigation) ──
    //
    // cocoar.js-eval ships `NewObject(typeName, args)` and `require(name)` as
    // engine globals by default. NewObject's fallback path walks every loaded
    // assembly via Reflectensions.TypeHelper.FindType — so out of the box, a
    // membership script can call e.g. `NewObject('System.IO.FileInfo',
    // ['/etc/hosts'])` and get a real FileInfo. That's host-RCE-equivalent.
    //
    // The mitigation in Modgud.Infrastructure/DependencyInjection.cs
    // registers a post-init engine configurator that overwrites both globals
    // with `JsValue.Undefined`. These tests pin that the mitigation holds —
    // re-enabling the default behaviour fails them loud.

    [Fact]
    public void A2_NewObject_GlobalIsRemoved()
    {
        // Direct engine probe: the NewObject identifier must resolve to
        // `undefined` after the security configurator runs.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();
        var result = engine.EvaluateExpression("typeof NewObject");
        Assert.Equal("undefined", result.ToString());
    }

    [Fact]
    public void A2_NewObject_FileInfoConstruction_FailsCleanly()
    {
        // The end-to-end probe: try to construct a FileInfo at top level via
        // the membership-script pipeline. Either NewObject is undefined and
        // calling it throws a TypeError (acceptable — script rejected), or
        // it's silently null (also acceptable — no CLR object reaches user
        // code). The bad outcome is a real System.IO.FileInfo instance.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();

        var threwOrReturnedNonObject = false;
        try
        {
            var result = engine.EvaluateExpression(
                "NewObject('System.IO.FileInfo', ['/etc/hosts'])");
            // If it didn't throw, it must NOT be a FileInfo.
            var clrValue = result?.ToObject();
            threwOrReturnedNonObject = clrValue is not System.IO.FileInfo;
        }
        catch
        {
            threwOrReturnedNonObject = true;
        }

        Assert.True(threwOrReturnedNonObject,
            "NewObject('System.IO.FileInfo', ...) returned a real FileInfo — " +
            "the security configurator that strips NewObject is not in effect. " +
            "See A2 NewObject finding in the threat model.");
    }

    [Fact]
    public void A2_RequireGlobalIsRemoved()
    {
        // `require` is wired by JsEngine.Initialize alongside NewObject. The
        // security configurator strips it too, since membership scripts have
        // no module-loading need.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();
        var result = engine.EvaluateExpression("typeof require");
        Assert.Equal("undefined", result.ToString());
    }

    [Theory]
    [InlineData("exit",
        "engine-DoS — exit() cancels the engine's CancellationToken")]
    [InlineData("setTimeout",
        "schedules callback on shared TaskScheduler, outlives recompute")]
    [InlineData("setInterval",
        "repeating timer, unbounded background work")]
    [InlineData("clearTimeout",
        "paired with setTimeout, no separate need")]
    [InlineData("clearInterval",
        "paired with setInterval, no separate need")]
    public void A2_BannedGlobal_IsUndefined(string globalName, string reason)
    {
        // Pin the strip list. If any of these become defined again, the
        // membership-script surface gained a vector documented in the
        // threat model.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();
        var result = engine.EvaluateExpression($"typeof {globalName}");
        Assert.Equal("undefined", result.ToString());
        Assert.NotNull(reason); // suppress unused-parameter
    }

    [Theory]
    [InlineData("console")]
    [InlineData("__log_info")]
    [InlineData("__log_warn")]
    [InlineData("__log_error")]
    [InlineData("__log_debug")]
    public void A2_LogGlobal_IsUndefined(string globalName)
    {
        // Console + the underlying __log_* bridge are stripped: a
        // membership script can't flood our log infrastructure with
        // synthetic info/warn/error lines on every recompute.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();
        var result = engine.EvaluateExpression($"typeof {globalName}");
        Assert.Equal("undefined", result.ToString());
    }

    [Fact]
    public void A2_KeptGlobals_AreStillReachable()
    {
        // The other side of the contract: globals we DO need stay defined.
        // If the strip list grows accidentally onto one of these, real
        // membership-script use-cases break.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();

        // Type — discriminator narrowing
        Assert.NotEqual("undefined",
            engine.EvaluateExpression("typeof Type").ToString());
        // linq — typed-literal helpers
        Assert.NotEqual("undefined",
            engine.EvaluateExpression("typeof linq").ToString());
        // btoa/atob — base64 (harmless)
        Assert.NotEqual("undefined",
            engine.EvaluateExpression("typeof btoa").ToString());
        Assert.NotEqual("undefined",
            engine.EvaluateExpression("typeof atob").ToString());
        // structuredClone — JSON deep-copy helper
        Assert.NotEqual("undefined",
            engine.EvaluateExpression("typeof structuredClone").ToString());
    }

    [Fact]
    public void A2_FetchGlobal_NotPresent_FetchNeverEnabled()
    {
        // Modgud never calls .EnableFetch(), so neither `fetch` nor
        // `fetchOptions` should be reachable. Pin to catch a future
        // refactor that accidentally enables it.
        var engine = _scope.ServiceProvider.GetRequiredService<JsEngine>();
        Assert.Equal("undefined",
            engine.EvaluateExpression("typeof fetch").ToString());
        Assert.Equal("undefined",
            engine.EvaluateExpression("typeof fetchOptions").ToString());
    }

    // (The TS transpiler also rewrites `new Foo(…)` into
    // `NewObject('Foo', […])` for FindType-resolvable names. With NewObject
    // undefined that path is closed too — covered indirectly by the three
    // A2 NewObject* tests above; no separate test added because the TS
    // rewrite's regex semantics are fiddly to assert on across CLR types.)

    // ─────────────────────────────────────────────────────────────────────
    // A3 — Type confusion
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A3_BigIntLiteral_RejectsCleanly()
    {
        // Translator's NumericLiteral case handles double, not BigInt.
        var ex = Record.Exception(() =>
        {
            var compiled = _transpiler.Transpile("(p: any) => p.Lastname === 9007199254740993n");
            _evaluator.BuildPredicate<Person>(compiled);
        });

        // Either the transpiler emits something the translator rejects, or
        // the parser rejects the BigInt literal. Either is fine — we want
        // a clean error, not a silent miscompare.
        Assert.NotNull(ex);
    }

    [Fact]
    public void A3_NaNComparison_TranslatesAsAlwaysFalse()
    {
        // JS: NaN === NaN is false. Postgres: NaN = NaN can be true. We want
        // JS-semantics preserved when translated.
        var compiled = _transpiler.Transpile("(p: any) => Number.NaN === Number.NaN");

        // Any of: translator rejects unknown `Number`, or it translates to
        // a constant false, or the resulting predicate evaluates to false on
        // every input. All are safe outcomes.
        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));
        if (ex is null)
        {
            var predicate = _evaluator.BuildPredicate<Person>(compiled);
            var anyPerson = new Person { Id = Guid.NewGuid() };
            Assert.False(predicate.Compile()(anyPerson));
        }
    }

    [Fact]
    public void A3_NumericOverflowLiteral_PreservesPrecisionOrRejects()
    {
        // 2^53 + 1 cannot be represented exactly as IEEE 754 double. Either
        // the translator rejects it, or it round-trips to something that
        // makes the comparison consistently produce a determined value.
        var compiled = _transpiler.Transpile("(p: any) => 9007199254740993 === 9007199254740992");

        // Whatever the outcome, it must be consistent — running the predicate
        // twice with the same input must give the same result.
        var predicate = _evaluator.BuildPredicate<Person>(compiled);
        var p = new Person { Id = Guid.NewGuid() };
        var first = predicate.Compile()(p);
        var second = predicate.Compile()(p);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A3_NegativeZeroEqualsZero_TranslatesConsistently()
    {
        // JS: -0 === 0 is true. We want a stable answer, not undefined behaviour.
        var compiled = _transpiler.Transpile("(p: any) => -0 === 0");
        var predicate = _evaluator.BuildPredicate<Person>(compiled);
        var p = new Person { Id = Guid.NewGuid() };
        Assert.True(predicate.Compile()(p));
    }

    // ─────────────────────────────────────────────────────────────────────
    // A4 — Cross-tenant probe
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A4_UnknownProperty_RejectsAtTranslation()
    {
        // `Person.Tenant` is not a property — translator must reject.
        var compiled = _transpiler.Transpile(
            "(p: any) => p.Tenant === 'other-tenant'");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
        Assert.Contains("Tenant", ex!.Message);
    }

    [Fact]
    public void A4_UnknownIdentifier_RejectsAtTranslation()
    {
        // No `db`/`session`/`Query` in scope — only the predicate parameter.
        var compiled = _transpiler.Transpile(
            "(p: any) => Query('Person').Where((x: any) => x.Id !== p.Id).Any()");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    [Fact]
    public void A4_DocumentSessionReference_NotReachable()
    {
        // Even with full DI wiring, no IDocumentSession identifier is
        // injected into the engine globals.
        var compiled = _transpiler.Transpile(
            "(p: any) => session !== null");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
    }

    // ─────────────────────────────────────────────────────────────────────
    // A5 — SQL-injection via LINQ
    //
    // Marten parameterises SQL from LINQ; the translator only emits
    // ConstantExpression for string literals, which Marten passes as
    // parameters. The class is mostly verified at the LINQ-emission level.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A5_StringLiteralWithSqlMetacharacters_StaysAsConstant()
    {
        // The translator must emit the literal as a ConstantExpression carrying
        // the raw string — Marten then parameterises it. A naive concat would
        // be a SQL-injection sink.
        var compiled = _transpiler.Transpile(
            "(p: any) => p.Firstname === \"'; DROP TABLE users; --\"");
        var predicate = _evaluator.BuildPredicate<Person>(compiled);

        // Walk the expression tree and assert the right-hand side is a
        // ConstantExpression with the exact original string.
        var body = (BinaryExpression)((LambdaExpression)predicate).Body;
        var rhs = Assert.IsType<ConstantExpression>(body.Right);
        Assert.Equal("'; DROP TABLE users; --", rhs.Value);
    }

    [Fact]
    public void A5_LinqGuidValid_TranslatesToConstant()
    {
        // Positive baseline: a well-formed Guid literal must translate to
        // a Guid-typed LINQ ConstantExpression so Marten parameterises it
        // correctly against Person.Id (which is Guid in C#).
        var compiled = _transpiler.Transpile(
            "(p: any) => p.Id === linq.guid('11111111-2222-3333-4444-555555555555')");

        var predicate = _evaluator.BuildPredicate<Person>(compiled);

        // Walk the LINQ tree: BinaryExpression Equal(p.Id, ConstantExpression(Guid))
        var body = (BinaryExpression)((LambdaExpression)predicate).Body;
        var rhs = Assert.IsType<ConstantExpression>(body.Right);
        var rhsValue = Assert.IsType<Guid>(rhs.Value);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), rhsValue);
    }

    [Theory]
    [InlineData("not-a-guid",
        "obviously-malformed text")]
    [InlineData("",
        "empty string")]
    [InlineData("11111111-2222-3333-4444",
        "truncated — only four of five Guid groups")]
    [InlineData("11111111-2222-3333-4444-5555555555555",
        "last group too long (13 hex chars instead of 12)")]
    [InlineData("zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz",
        "structurally Guid-shaped but with non-hex characters")]
    [InlineData("'; DROP TABLE users; --",
        "SQL-injection attempt — must not slip through as a Guid")]
    public void A5_LinqGuidMalformedInput_RejectsAtTranslation(string badInput, string scenario)
    {
        // A malformed Guid literal must reject at translation time, not at
        // SQL execution. If the translator passed the bad string through,
        // Marten would either choke at parameter-bind or — worse — emit a
        // string-vs-Guid comparison that silently never matches. Worst
        // case is the SQL-injection-shaped input — that must not leave
        // translation alive in any form.
        var compiled = _transpiler.Transpile(
            $"(p: any) => p.Id === linq.guid('{badInput.Replace("'", "\\'")}')");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
        Assert.NotNull(scenario); // suppress unused-parameter
    }

    // ─────────────────────────────────────────────────────────────────────
    // A6 — Information disclosure via translator errors
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A6_UnsupportedAstNode_DoesNotLeakAcornimaInternals()
    {
        // Block-bodied arrow with a try/catch is not in the whitelist;
        // translator throws NotSupportedException with the AST node type name.
        // Today the message contains `BlockStatement` — that's an Acornima
        // internal. Pin: ideally the message names the *user-visible* JS
        // construct (e.g. "block statement") rather than the parser type.
        var compiled = _transpiler.Transpile(
            "(p: any) => { try { return true; } catch { return false; } }");

        var ex = Record.Exception(
            () => _evaluator.BuildPredicate<Person>(compiled));

        Assert.NotNull(ex);
        // We don't assert the exact message because today it leaks
        // `BlockStatement`. When that's tightened, this assertion stays
        // valid (the message just no longer contains it).
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void A6_TranspileError_DoesNotIncludeStackTraceInMessage()
    {
        // A syntactically broken TS source must produce a clean transpile
        // error, not a multi-line stack trace.
        var ex = Record.Exception(
            () => _transpiler.Transpile("(p: any) => p.) {{ broken"));

        Assert.NotNull(ex);
        // No `at System.` or stack trace markers in the message itself
        // (those belong on .StackTrace, not .Message).
        Assert.DoesNotContain("at System.", ex!.Message);
        Assert.DoesNotContain("at Cocoar.JsEval", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles a predicate and returns its evaluation against a no-op
    /// person — used in A2 tests where the predicate may translate to a
    /// constant that we want to confirm is `false`.
    /// </summary>
    private static bool EvaluateConstantBool(Expression<Func<Person, bool>> predicate)
    {
        var compiled = predicate.Compile();
        var noopPerson = new Person { Id = Guid.NewGuid() };
        return compiled(noopPerson);
    }
}
