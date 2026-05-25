---
title: Critter Stack 2026 — Marten 9 / Wolverine 6
description: Migration gotchas and patterns post-Marten-9 / Wolverine-6 upgrade.
---

# Critter Stack 2026 — Marten 9 / Wolverine 6

Modgud shipped the Critter-Stack-2026 backport on **2026-05-24**, aligning
with [Cocoar.AppBase v2.1.0](../../). The migration carries a handful of
gotchas every backend contributor (and every future AppBase backport) should
know about. This page is the canonical reference.

## Status & versions

Pinned in `src/dotnet/Directory.Packages.props`:

| Package | Version |
| --- | --- |
| `Marten` | 9.0.0 |
| `Marten.AspNetCore` | 9.0.0 |
| `WolverineFx.Marten` | 6.0.0 |
| `JasperFx.Events.SourceGenerator` | 2.0.0 |
| `WolverineFx.RuntimeCompilation` | 6.0.0 |

The two V8 event-store defaults Modgud still pins (and the reasoning for
each) are in
[`MartenConfiguration.ConfigureEventStore`](https://github.com/cocoar-dev/modgud/blob/develop/src/dotnet/Modgud.Infrastructure/Persistence/Marten/Configuration/MartenConfiguration.cs)
at lines 120-125:

```csharp
// AppendMode Rich (vs 9.x default QuickWithServerTimestamps) — RaiseSideEffects
// on UserViewProjection assumes Rich-timing.
options.Events.AppendMode = EventAppendMode.Rich;

// UseIdentityMapForAggregates false (vs 9.x default true) — 9.x can leak
// self-mutations via events within the same batch; principal projection +
// view projections rely on snapshot freshness.
options.Events.UseIdentityMapForAggregates = false;
```

## Gotcha 1 — Source-gen applies to ALL `Apply`/`Create` classes

::: warning
Marten 9 source-generates the apply/create dispatchers for **every** class with
`Apply(…Event)` / `Create(…Event)` methods — not just classes whose names end
in `Projection`. Live aggregates accessed via
`session.Events.AggregateStreamAsync<T>(streamId)` are silently wrapped in a
synthetic `SingleStreamProjection<T, Guid>` that needs the source-generated
dispatcher.
:::

### Two requirements per class

1. The class itself **must be `partial`**.
2. The owning `.csproj` **must reference the source generator**:

```xml
<PackageReference Include="JasperFx.Events.SourceGenerator">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

### Where this bit us

The initial migration grepped for `class \w+Projection` and added the analyzer
to `Modgud.Infrastructure`, `Modgud.Authentication`, and
`Modgud.Authorization`. That missed three OAuth aggregates in
`Modgud.Domain` that are used as **live aggregates** rather than named
projections:

- `OAuthApplicationAggregate` (`src/dotnet/Modgud.Domain/OAuth/Applications/OAuthApplicationAggregate.cs`)
- `OAuthScopeAggregate` (`src/dotnet/Modgud.Domain/OAuth/Scopes/OAuthScopeAggregate.cs`)
- `OAuthApiAggregate` (`src/dotnet/Modgud.Domain/OAuth/Apis/OAuthApiAggregate.cs`)

All three are `public partial class …Aggregate` and define `Apply` / `Create`
methods. Without the analyzer in `Modgud.Domain.csproj`, Marten 9 threw
"No source-generated dispatcher found" the first time
`AggregateStreamAsync<OAuthApplicationAggregate>(id)` ran.

The fix is the analyzer reference now visible in
[`Modgud.Domain.csproj`](https://github.com/cocoar-dev/modgud/blob/develop/src/dotnet/Modgud.Domain/Modgud.Domain.csproj) lines 14-17.

### Grep pattern that catches everything

Don't grep for the `Projection` suffix. Grep for the method signatures:

```bash
rg -n 'public\s+(void|.*?)\s+(Apply|Create)\s*\(' src/dotnet
```

Cross-check the result list against the analyzer-referencing csprojs. Anything
on the result list whose csproj is missing the analyzer is a latent runtime
crash waiting for the first `AggregateStreamAsync` call.

## Gotcha 2 — Wolverine 6 service-location allowlist

Wolverine 6 made `ServiceLocationPolicy.NotAllowed` the **default**. Any
handler that resolves a service from `IServiceProvider` at runtime now needs
to be on an explicit allowlist or codegen fails loudly. Modgud keeps the
strict default — accidental new service-location dependencies are caught at
boot.

The current allowlist lives in `Modgud.Api/Program.cs` lines 805-815:

```csharp
opts.CodeGeneration.AlwaysUseServiceLocationFor<UserManager<ApplicationUser>>();
opts.CodeGeneration.AlwaysUseServiceLocationFor<SignInManager<ApplicationUser>>();
opts.CodeGeneration.AlwaysUseServiceLocationFor<Cocoar.JsEval.IJsModuleBuilder>();
```

`UserManager` / `SignInManager` are non-negotiable — they take `IServiceProvider`
in their constructors by design (resolving `IPasswordHasher<T>` and
`IUserValidator<T>`). Cocoar.JsEval 4.1 collapsed the previous transitive
service-location entries (`IMembershipEvaluator`, `IAutoMembershipRecalculator`)
into a single one at the JsEngine's module-builder seam.

**Rule:** if you add a handler that takes `IServiceProvider` (or resolves
something only via `IServiceProvider.GetRequiredService<T>()`), add the type to
this allowlist with a one-line comment explaining why it can't be plain
constructor-injected.

## Gotcha 3 — `WolverineFx.RuntimeCompilation` is required

Wolverine 6 decoupled the Roslyn runtime codegen from core. Without
`WolverineFx.RuntimeCompilation` in the project that hosts Wolverine handlers,
`IMessageBus.InvokeAsync<T>` fails at runtime — the generated handler classes
never compile.

`Modgud.Infrastructure.csproj` line 22 references it explicitly. Any
future project that adds Wolverine handlers needs the same reference.

## Gotcha 4 — Inline-projection Store/Events ordering

Modgud proactively fixed this **before** Marten 8.34 made it the
documented pattern, so the code already reflects the corrected shape. The
trap, restated for new contributors:

For an **inline projection** that produces a doc the same transaction also
`Store`s directly, the call order matters:

```csharp
// CORRECT
session.Store(doc);                          // first
session.Events.Append(streamId, event);      // second — projection writes after
await session.SaveChangesAsync();

// WRONG — the inline projection runs during Append and writes a stale
// doc that the subsequent Store gets clobbered by.
session.Events.Append(streamId, event);
session.Store(doc);
await session.SaveChangesAsync();
```

Search for the pattern with `rg "session\.Events\.Append" src/dotnet` — look at
`RealmAdminBootstrapper`, `RolesEndpoints`, and the test factory for the
canonical examples.

## Gotcha 5 — `Principal` sub-class double-registration

::: warning
A new `Principal` subclass needs **two** registrations or it silently lands in
its own Marten table and is invisible to cross-type queries.
:::

1. **Marten** — `AddSubClass<NewPrincipalSubClass>("alias")` in
   `Modgud.Authorization/Setup/MartenStoreOptionsExtensions.cs` (lines
   46-48) so the doc lands in `mt_doc_principal` instead of
   `mt_doc_newprincipalsubclass`.
2. **System.Text.Json** — `[JsonDerivedType(typeof(NewPrincipalSubClass), "alias")]`
   on the `Principal` base type, so the polymorphic serializer can round-trip
   cross-type reads.

Without both, the new subclass lands in its own table and **silently** drops
out of:

- The Group-Picker BFS (queries `mt_doc_principal` for any principal type).
- JsEval membership scripts (also query against `Principal`).
- Any `session.Query<Principal>()` call anywhere in the codebase.

Discovered 2026-05-24 during Phase-2C `ServiceAccount` smoke testing — the new
sub-class compiled, persisted, was readable by direct id-lookup, and was
completely invisible to the Group-Picker. The two-registration pattern is now
the rule.

## Upgrade checklist for new Marten/Wolverine majors

When AppBase pushes the next Critter-Stack baseline:

1. **Read the AppBase migration guide first.** Path:
   `C:\git\cocoar\Cocoar.AppBase\docs\migrations\v<N-1>-to-v<N>.md` —
   AppBase is the source-of-truth template and lists every breaking change
   already validated against a clean baseline.
2. **Bump versions in `Directory.Packages.props`.** Marten, Marten.AspNetCore,
   WolverineFx.Marten, JasperFx.Events.SourceGenerator, WolverineFx.RuntimeCompilation.
3. **Re-evaluate the V8-pins** in `ConfigureEventStore` (lines 120-125). Each
   was walked back individually so it can be turned off later when the rest
   of the code is ready.
4. **Re-grep for `Apply` / `Create` methods** across the solution; cross-check
   csproj analyzer coverage. Any `partial` class with those signatures whose
   csproj has no `JasperFx.Events.SourceGenerator` reference is a runtime
   crash.
5. **Re-grep for `AlwaysUseServiceLocationFor`** in `Program.cs` — when
   Wolverine changes its default again, the allowlist may need entries (or
   be redundant).
6. **Run `dotnet test`** — the architecture tests in
   `Modgud.Tests.Unit/Architecture/` enforce some of the discipline
   above and will fail noisily on regressions.
7. **Smoke-test `AggregateStreamAsync` paths.** The OAuth-aggregate dispatcher
   gap from 2026-05-24 didn't show in unit tests — it surfaced the first time
   the OAuth admin UI loaded an aggregate.

## See also

- [Marten `RaiseSideEffects` — Gotchas](./marten-raise-side-effects) — the
  sibling page for projection side-effects.
- AppBase v2.1.0 — `C:\git\cocoar\Cocoar.AppBase\docs\migrations\v1-to-v2.md`
  for the full Critter-Stack-2026 backport checklist this migration followed.
