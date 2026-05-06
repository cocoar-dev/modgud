---
title: JsEval Threat Model
---

# JsEval Threat Model — Membership Scripts

::: warning Internal engineering log
This page is the threat-model and surface-mapping document that drives
the JsEval-Fuzzing initiative (Phase 1 → Phase 2 → Phase 3). It
references internal type names, package internals (Acornima, Jint),
and code paths that are not part of the public product surface. Public
consumers don't need this file — see
[Concepts: Authorization](../concepts/groups-and-authorization) for the
admin-facing documentation of auto-membership scripts.
:::

**Date:** 2026-05-06 · **Scope:** auto-membership scripts (`Group.MembershipScript`) and any future script-driven feature reusing the same pipeline. · **Status:** ✅ Phases 1-3 complete. All six findings (F1-F6) closed lib-side in Cocoar.JsEval 4.0; Cocoar.Auth runs both layers (lib defaults + consumer-side belt-and-braces). 41 pinning tests in `MembershipSecurityTests`, zero skip-trackers.

## Pipeline (the actual code path)

```
Admin writes TS in the SPA Group editor
  ↓ POST /api/admin/groups
CreateGroupCommand.Handle (Cocoar.Auth.Authorization/Commands/CreateGroupCommand.cs)
  ↓
TsTranspiler.Transpile(typeScript) → compiledJs : string
  ↓ stored on Group.CompiledMembershipScript
… time passes, user/group events fire …
  ↓
MembershipRecomputer (auto-recompute on UserCreatedEvent etc.)
  ↓
MembershipEvaluator.BuildPredicate<Person>(compiledJs)
  ├─ Stage B1: jsEngine.EvaluateExpression(compiledJs)
  │            ← Jint EXECUTES top-level JS, returns the function value
  └─ Stage B2: JsExpressionTranslator.Translate<Person, bool>(jsFn, engine, options)
               ← walks the function's Acornima AST → emits LINQ expression-tree
  ↓ predicate : Expression<Func<Person, bool>>
session.Query<Person>().Where(predicate)         ← Marten translates LINQ → SQL → Postgres
```

Two distinct "user code runs" points:

- **Stage A (transpile)** — `TsTranspiler` runs the embedded TypeScript compiler **inside Jint** to convert TS→JS. The TS compiler itself is JS code; user-supplied TS becomes input to that JS code.
- **Stage B1 (evaluate-expression)** — Jint executes the compiled JS at top level to obtain the function value. **This is the moment the script can run side-effects** — assign to globals, mutate prototypes, throw, hang.
- **Stage B2 (translate)** — walks the function's `FunctionDeclaration.Body` AST. Pure inspection — no JS execution at this point.

## Trust boundaries

### Today

- **Authentication required** — all script-create paths sit behind `cocoar-auth:group:write` permission, gated by `RequiresPermission` middleware.
- **Realm-Admin or higher** — the only role that holds `cocoar-auth:group:write` in default seeding is the System Admin (`realm:admin`). Realm-Admins are by current threat model "trusted authenticated principals".
- **Defense-in-depth nonetheless required** — a compromised admin account, an XSS bug elsewhere that hijacks an admin session, or simple bug-on-our-side script writing should not be allowed to escalate to host-level RCE, cross-tenant data access, or DoS of the IdP.

### Tomorrow (when Tenant-Admins author scripts)

- **Privilege boundary** — at that moment, any escape from JsEval becomes a real privilege escalation: tenant-admin (limited to their tenant) → Cocoar.Auth host (cross-tenant, host RCE).
- This document is written so the test surface is solid *before* that boundary tightens, not after.

## Engine configuration (as wired in Cocoar.Auth today)

`Cocoar.Auth.Infrastructure/DependencyInjection.cs:196-201`:

```csharp
services.AddJsEval(b => b
    .AddLinq()
    .AddDiscriminatorMappings<Principal>("Type",
        ("person", typeof(Person)),
        ("group", typeof(Group)),
        ("service-account", typeof(ServiceAccount))));
```

What this gives us, what it doesn't:

| Property | State | Risk |
|---|---|---|
| `AllowClr(...)` | **NOT called** | ✓ User scripts can't `System.IO.File.ReadAllText(...)` — Jint blocks CLR access by default. |
| `AddExtensionMethods(...)` | not called by Cocoar.Auth itself, but `AddLinq()` adds `linq.*` helpers (typed-literal coercions etc.) | low — those helpers are pure value constructors |
| `EnableFetch()` | **NOT called** | ✓ `fetch()` global not available — no outbound HTTP from inside scripts. |
| `JintOptions.CatchClrExceptions()` | **on** | ✓ CLR exceptions caught inside engine, prevents Jint internal state corruption from bubbling exceptions. |
| `AllowOperatorOverloading()` | **on** | low — only operator semantics on user types, no security impact. |
| `ExperimentalFeatures = TaskInterop` | **on** | medium — JS `await` on a .NET Task works. Combined with no `AllowClr`, the only Tasks reachable are those Cocoar.JsEval internally exposes (fetch when enabled, timers). Today: low. |
| **TimeoutInterval / Constraints** | **NOT set** | 🟠 **Real risk.** No automatic timeout, no max-statements, no recursion-depth limit, no memory cap. A `while(true){}` or `(()=>f(f))(()=>f(f))` script will hang the thread executing the recompute until ASP.NET Core kills the request (~100s default), wasting a worker. |
| `CancellationToken` | wired but **never triggered automatically** | mitigates the above only if the caller cancels — which `MembershipRecomputer` does not. |

## Engine globals — the full surface

Every value `JsEngine.Initialize()` registers becomes reachable from
the top-level Stage B1 execution of any membership script. The full
inventory + the consumer-side strip decision:

| Global | Source | Risk | Decision |
|---|---|---|---|
| `NewObject` | `JsEngine.cs:121` | 🚨 critical — assembly walk → arbitrary CLR construction | **stripped** |
| `require` | `JsEngine.cs:122` | 🚨 critical — module loading | **stripped** |
| `exit` | `JsEngine.cs:120` | engine-DoS — cancels engine CT, future calls fail | **stripped** |
| `setTimeout` / `setInterval` / `clearTimeout` / `clearInterval` | `RegisterTimers` | async pollution, work outlives the recompute on shared TaskScheduler | **stripped** |
| `__log_info` / `__log_warn` / `__log_error` / `__log_debug` + `console` | `RegisterConsole` + ConsoleScript shim | log-spam — any admin-authored script can flood the ops log infra | **stripped** |
| `Type` (`JsTypeGlobal`) | `JsEngine.cs:134` (set when DiscriminatorMappings or TypeAliases non-empty) | needed for `Type.Is(p, 'person')` discriminator narrowing | **kept** |
| `linq` (`LinqGlobal`) | `JsEval.Linq/LinqCasts.cs:56` | typed-literal helpers (`linq.guid("…")`) used by membership scripts | **kept** |
| `btoa` / `atob` | `RegisterWebApis` | base64 encode/decode, pure string ops | kept (harmless) |
| `__perf_now` + `performance` shim | `RegisterWebApis` + `PerformanceScript` | timing only, no privileged surface | kept (harmless) |
| `__te_encode` / `__td_decode` + `TextEncoder`/`TextDecoder` shim | `RegisterWebApis` + `TextEncoderDecoderScript` | UTF-8 round-trip | kept (harmless) |
| `structuredClone` | `StructuredCloneScript` (`JSON.parse(JSON.stringify(...))`) | pure data round-trip | kept (harmless) |
| `fetch` / `fetchOptions` | `Fetch.FetchHandler.Register` — only when `EnableFetch()` is called | network egress, not enabled in Cocoar.Auth | confirmed absent (test pin) |
| `CsDateTime` | `CsDateTimeGlobals.Register` — only when configurator wires it | ctor-style DateTime, not wired in Cocoar.Auth | n/a |

The strip is implemented in
`Cocoar.Auth.Infrastructure/DependencyInjection.cs` via
`RegisterEngineConfigurator` and pinned by the
`MembershipSecurityTests.A2_*Global*` tests. Anything that grows the
surface needs an addition both to that configurator and to the
pinning suite.

## Translator surface (what it accepts)

`JsExpressionTranslator.Visit` (line 173 in `cocoar.js-eval`) is a **whitelist** dispatch:

```csharp
private static LinqExpr Visit(AstExpr node, Context ctx) => node switch
{
    Identifier                     => resolve param/namespace/type
    MemberExpression               => property access (resolved via reflection)
    CallExpression                 => method call (resolved via IJsMethodMap)
    ChainExpression                => optional chaining
    StringLiteral / NumericLiteral / BooleanLiteral / NullLiteral
    NonLogicalBinaryExpression     => +, -, *, /, %, <, >, ==, !=, …
    LogicalExpression              => &&, ||
    UnaryExpression                => !, -, +, typeof
    ConditionalExpression          => a ? b : c
    ArrowFunctionExpression        => only inside method calls (e.g. .Where(x => ...))
    _                              => throw NotSupportedException
};
```

**Anything not on the list throws `NotSupportedException` at translation time** — including but not limited to:

- `AssignmentExpression` (`=`, `+=`, …)
- `VariableDeclaration` / `let` / `const` inside the body
- `BlockStatement` (multi-statement function bodies)
- `IfStatement`, `ForStatement`, `WhileStatement`, `SwitchStatement`
- `TryStatement`, `ThrowStatement`
- `NewExpression`, `ThisExpression`, `Super`
- `ObjectExpression`, `ArrayExpression`
- `TemplateLiteral`, `TaggedTemplateExpression`
- `SpreadElement`, `RestElement`
- `YieldExpression`, `AwaitExpression`
- `ImportExpression`, `MetaProperty`
- `ChainExpression` with calls (`x?.()` is rejected explicitly)

That's a strong default-deny on the AST shape. **The translator itself is unlikely to be the surface for an escape** — the practical attack must use only whitelisted nodes.

## Realistic attacker classes

### A1 · Resource exhaustion (DoS)

Practical, because nothing limits Jint's execution cost in our wiring. Any of these will likely hang or OOM the host:

```js
// Top-level infinite loop (Stage B1 hangs):
while(true){}
() => true
```

```js
// Allocation flood at top level:
let s = "x"; for(let i=0;i<100;i++) s += s;
() => true
```

```js
// Translator-side: deeply-nested ternary forces deep recursion in Visit
() => a ? b ? c ? d ? ... : 1 : 1 : 1 : 1
```

::: tip Expected behaviour the suite should pin
Stage B1 must complete in bounded time. Today: **it doesn't**. Mitigation candidates are listed under [Phase 3](#phase-3-plan-after-phase-2-results).
:::

### A2 · Native-host escape

The high-value class: can a script reach `System.IO`, Jint internals, or another tenant's data?

Checks the suite must run:

- `eval("...")` and `Function("...")` — Jint blocks both by default; verify.
- `globalThis.process` / `globalThis.require` — neither exposed by Jint, verify they are `undefined`.
- `__proto__` and prototype-chain manipulation — `({}).__proto__.toString = () => leak()` — does this affect *subsequent* engine calls for the same scoped JsEngine instance? Cocoar.Auth's JsEngine is **`Scoped` lifetime** so per-request, but `JsLinqContext.Scope(...)` shares the underlying engine across the recompute.
- `Array.prototype.join.call(this, ...)` and similar prototype borrowing — does it surface CLR types when `this` is a CLR object?
- `import("file:///...")` / `import("System.IO.File")` — the translator rejects `ImportExpression` in the lambda body, but Stage B1 evaluates top-level — does it accept top-level `import()`?
- Type-conversion gymnastics — `({valueOf: () => leak()})` passed to a property comparison — does the translator emit code that calls `valueOf` at host runtime?

::: danger Finding A2-NewObject (critical) — closed in Cocoar.Auth, lib-side fix pending
`Cocoar.JsEval.Engine.JsEngine.Initialize` registers two globals
unconditionally:

```csharp
_engine.SetValue("NewObject", new Func<string, object[], object?>(ResolveAndCreate));
_engine.SetValue("require",   new Func<string, JsValue>(Require));
```

`ResolveAndCreate` first consults the configured `TypeAliases`, then
falls back to `Cocoar.Reflectensions.Helper.TypeHelper.FindType` —
**which walks every loaded assembly via
`AppDomain.CurrentDomain.GetAssemblies()` and returns the first matching
type**. A membership script can therefore call e.g.
`NewObject('System.IO.FileInfo', ['/etc/hosts'])` at the top level and
get a real `FileInfo` instance, or `NewObject('System.Diagnostics.Process')`
followed by `.Start(...)` for arbitrary process spawning — anything
with a public constructor in any loaded assembly. This is host-RCE
equivalent for anyone holding `cocoar-auth:group:write` and bypasses
every Translator-side check (it executes during Stage B1 before Stage
B2 even runs).

The TS transpiler additionally rewrites `new Foo(...)` →
`NewObject('Foo', [...])` whenever `FindConstructorReplaceType(Foo)`
resolves, so a realistic attacker doesn't need to know the
`NewObject` API name — `new System.Net.Http.HttpClient()` reaches the
same vector.

**Cocoar.Auth-side mitigation (commit `<below>`):** the
`AddJsEval(...)` builder in `Cocoar.Auth.Infrastructure.DependencyInjection`
now calls `RegisterEngineConfigurator(engine => { engine.SetValue("NewObject",
JsValue.Undefined); engine.SetValue("require", JsValue.Undefined); })`.
Configurators run after `JsEngine.Initialize`, so the unsafe defaults
are overwritten with `undefined` before any user script sees the engine.
Membership scripts are pure predicates — they have no need for either
global. Pinned by three tests in `MembershipSecurityTests.A2_NewObject_*`.

**Lib-side action pending:** file an upstream issue in `cocoar.js-eval`
asking that `NewObject`'s assembly-walking fallback become opt-in
(e.g. `b.AllowAnyClrType()` or only when `AllowClr` was called). The
current default is unsafe-by-default for any consumer that doesn't
explicitly know to override.
:::

### A3 · Type confusion

Smaller class. Edge cases that might bypass the translator's narrowing logic:

- Numeric literal overflow — `9007199254740993` (above `Number.MAX_SAFE_INTEGER`). Does it round-trip through `LinqExpr.Constant(double)` correctly, or does Marten emit a SQL value that doesn't match the C# `long`?
- `NaN === NaN` — translator emits `Equal(Constant(NaN), Constant(NaN))`. Postgres comparison semantics differ from JS; predicate may be undecidable.
- `BigInt` literals (`123n`) — unsupported in `NumericLiteral` case? Should reject cleanly.
- Negative zero (`-0 === 0`) — JS true, SQL ?
- `Symbol("x") === Symbol("x")` — translator accepts as a `CallExpression` on Symbol?

### A4 · Cross-tenant probe

The threat: can a script reference a Person in a different tenant, or query a different tenant's DB?

- LINQ predicate runs against `IDocumentSession` injected by `TenantedSessionFactory`; session is tenant-scoped. The predicate body has access only to a single `Person` parameter, no DB reference, no `IDocumentSession`. **Architecturally tenant-isolated.**
- Verify by attempt — a predicate referencing `Person.Tenant` (no such property), `Person.Realm` (no such property), or trying to access `linq.Query(...)` if such a global existed. Translator's `VisitIdentifier` should reject any unknown identifier.

### A5 · SQL injection via LINQ

Marten translates LINQ to parameterized SQL. The translator's `LinqExpr.Constant(string)` produces a parameter — string content cannot escape the parameter context. But:

- `linq.guid("...")` — what if the string is malformed? Should fail at translation time, not at SQL.
- String method calls — `Person.UserName.StartsWith(userInput)` — pinned safe by Marten/Postgres `LIKE` parameterisation.
- The CodeQL `cs/sql-injection` finding sits at the unrelated `CREATE DATABASE` site, not in the LINQ pipeline.

### A6 · Information disclosure via translator errors

When a script is invalid, what does the error message expose?

- `InvalidOperationException("Property 'X' not found on Person")` — leaks the type name `Person` and confirms it's the CLR identifier. Acceptable for admin-facing UI today; problematic when tenant-admins can author scripts.
- `NotSupportedException("AST node {node.GetType().Name} not supported")` — leaks Acornima internal node types (e.g., `BlockStatement`). Low impact, but a tighter message would still be useful.
- Stack traces — if a translator bug surfaces an unhandled exception, the stack contains internal types. Mitigated today by `CreateGroupHandler.catch (Exception ex)` returning a sanitised `Validation` error.

## Pinned baseline assumptions

The Phase-2 suite verifies that all of these hold:

1. **Translator default-deny holds** — any AST node not in the whitelist throws cleanly.
2. **No CLR escape** — `System.IO`, `System.Reflection`, `System.Diagnostics.Process` not reachable from any script form.
3. **No fetch / no module import** — `fetch`, `import()`, `require` either undefined or rejected at translation time.
4. **Jint sandbox boundaries hold** — `eval`, `Function`, `globalThis.process` not callable / not present.
5. **Translation is pure inspection** — walking the AST does not cause user-code execution. (Stage B1 already executed the top-level script; Stage B2 only inspects the resulting function value's AST.)
6. **DB query stays tenant-scoped** — a translated predicate run via `IDocumentSession.Query<Person>()` only reaches the current tenant's DB, never cross-tenant — guaranteed by `TenantedSessionFactory`, not by the translator.

## Identified gaps even before fuzzing

These are findings already from the surface scan, before a single adversarial test runs:

::: tip Gap-1 (high) · CLOSED — wall-clock budget + cooperative cancel
`MembershipEvaluator.BuildPredicate` now wraps the Jint evaluation in
a 2 s wall-clock budget combined with the caller's `CancellationToken`,
linked through `CancellationTokenSource.CreateLinkedTokenSource`. The
linked token's `Register` calls `jsEngine.Stop()` on fire, which
cancels Jint's internal CTS and aborts the next executed statement.
A `while(true){}` at top level now surfaces as
`OperationCanceledException` after ≤2 s instead of hanging the worker.
Pinned by `MembershipSecurityTests.A1_TopLevelInfiniteLoop_TimesOutWithin3s`
and `A1_TopLevelAllocationFlood_TimesOutOrCompletes`.
:::

::: tip Gap-2 (medium) · CLOSED — input length cap
`Cocoar.Auth.Authorization.Membership.ScriptInputLimits` defines a
16 KiB cap and a `Validate(script, errorCode)` helper. Applied at
`CreateGroupCommand`/`UpdateGroupCommand` for `MembershipScript` and
at `UpdateLoginProviderCommand` for `UserUpdateScript`. Multi-MiB
TS payloads are rejected before the TS compiler ever sees them.
Pinned by `MembershipSecurityTests.A1_OneMegabyteScript_RejectedByLengthCap`.
:::

::: tip Gap-3 (medium) · CLOSED LIB-SIDE in Cocoar.JsEval 4.0 — plus consumer-side belt-and-braces
**Lib-side fix shipped**: Cocoar.JsEval 4.0 (final fix landed in
3.4.0-beta.4 prerelease) added a pre-parse
nesting-depth scan to `TsTranspiler.Transpile` (default
`MaxParseDepth = 128`). A 500-deep ternary input now throws a
controlled `TsTranspileException` instead of escalating to
StackOverflowException. Verified: depths 50/100 succeed, depths
300/500/1000 all surface as `TsTranspileException`. Pinned by
`A1_TranspilerDepthCap_LibSideClosed`.

**Consumer-side belt-and-braces still in place**:
`ScriptInputLimits.MaxNestingDepth = 50` — a pre-parse scan in
`ScriptInputLimits.Validate` (Authorization slice) walks the source
counting unmatched parens/braces/brackets (skipping string literals
and comments) and rejects inputs over depth 50 with a
`*ScriptTooDeep` error. The consumer threshold (50) is tighter than
the lib's (128), so it fires first; the lib's cap is the safety net
when something bypasses our validator. Reasons to keep both:

- Domain-specific error code (`Group.MembershipScriptTooDeep` vs
  generic `TsTranspileException`).
- Synchronous fast-fail before the TS pipeline boots Jint.
- Decouples our threshold from the lib version.

Pinned by `A1_DeeplyNestedTernary_RejectedByDepthCap` and
`A1_DepthCounter_HandlesStringsAndComments`. Applied at all three
consumer entry points: `CreateGroupCommand`, `UpdateGroupCommand`,
`UpdateLoginProviderCommand`.

**Trust-model context**: Tenant-Admins will author membership
scripts in the upcoming product surface, so the cross-tenant-DoS
path was a real concern (a single tenant-admin's 500-deep ternary
crashing the IdP for every tenant). Now closed at both layers.
:::

::: tip Gap-4 (low) · CLOSED — cancellation plumbed end-to-end
`IMembershipEvaluator.BuildPredicate<T>` now takes
`CancellationToken ct = default`. `AutoMembershipRecalculator` and
`EffectiveGroupsResolver` propagate their existing CT into every call.
The token is linked with the wall-clock CTS from Gap-1's fix, so
caller-cancellation and timeout share the same cooperative-stop
mechanism via `jsEngine.Stop()`.
:::

::: danger Gap-5 (critical, mitigated consumer-side) · NewObject + require globals expose all CLR types
See A2-NewObject finding above. `cocoar.js-eval` registers `NewObject`
and `require` as engine globals unconditionally; `NewObject`'s
fallback walks every loaded assembly, allowing arbitrary CLR-type
construction including `Process`, `FileInfo`, `HttpClient`, etc.
**Closed in Cocoar.Auth via post-init engine configurator** that
overwrites both globals with `JsValue.Undefined`. Lib-side fix should
make assembly-walking fallback opt-in.
:::

All five gaps are closed (see banner cluster above). The mix of
lib-side and consumer-side fixes:

- Gap-1, Gap-3, Gap-5 (NewObject) — lib-side in Cocoar.JsEval 4.0
  (engine globals safe-by-default, translator + parser depth-caps,
  WithExecutionTimeout builder flag).
- Gap-2 — consumer-side in `ScriptInputLimits.MaxScriptCharacters`.
- Gap-4 — consumer-side: `IMembershipEvaluator.BuildPredicate` takes
  `CancellationToken` propagated by `AutoMembershipRecalculator` and
  `EffectiveGroupsResolver`.

## Phase 2 — adversarial test suite (shipped)

`Cocoar.Auth.Tests.Unit/Authorization/MembershipSecurityTests.cs`.
Six categorised test groups; total **41 tests**, zero skips.

| Group | Goal | Tests |
|---|---|---|
| A1 Resource exhaustion | Stage B1 terminates in bounded time; length-cap and depth-cap reject before parser runs | 5 |
| A2 Native escape | Jint sandbox holds; no CLR / fetch / import / eval / process; `NewObject`+`require`+`exit`+timers+`console`+`__log_*` all `undefined` by lib default | 17 |
| A3 Type confusion | Numeric edge cases (NaN, BigInt, overflow, -0) translate or reject cleanly | 4 |
| A4 Cross-tenant probe | Unknown identifiers / properties reject; predicate has no DB-reference path | 3 |
| A5 SQL via LINQ | Parameterised constants only; `linq.guid("…")` validates input across six malformed-input shapes including SQL-injection attempts | 8 |
| A6 Info disclosure | Error messages don't expose Acornima internals or upstream stack traces | 2 |

Each test runs in &lt; 1 s. Suite duration is dominated by setup, not scripts.

## Phase 3 — gap closure (shipped)

The lib-vs-consumer split landed roughly as the table above suggests:

- **Lib-side fixes** in Cocoar.JsEval 4.0 — engine globals
  safe-by-default, `WithExecutionTimeout` / `WithMaxStatements`
  builder flags, translator depth-cap (`MaxAstDepth`) and parser
  depth-cap (`MaxParseDepth`).
- **Consumer-side fixes** in Cocoar.Auth —
  `Cocoar.Auth.Authorization.Membership.ScriptInputLimits` for
  length/depth caps with domain-specific error codes,
  `MembershipEvaluator.BuildPredicate` with a `CancellationToken`
  parameter that registers `jsEngine.Stop()` on cancel, length+depth
  validation at every consumer entry point
  (`CreateGroupCommand`, `UpdateGroupCommand`,
  `UpdateLoginProviderCommand`).

Each gap has at least one pinning test in
`MembershipSecurityTests.A1_*` / `A2_*` so a regression in either
layer surfaces fast.
