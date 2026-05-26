# Marten `RaiseSideEffects` — Gotchas

Modgud uses Marten's `RaiseSideEffects` override on the
`UserViewProjection` (async `MultiStreamProjection`) to publish SignalR
`Created` / `Updated` / `Deleted` events whenever a `UserView` document
changes. The pattern is small, works, and tests cleanly — but the
Marten library hides two silent traps that anybody extending this
pattern is likely to hit, so they are documented here as a tripwire.

Captured during the Critter-Stack-2026 backport — both traps cost
hours of debugging the first time they bit.

## Gotcha 1 — Inline projections silently swallow `RaiseSideEffects`

By default, Marten only invokes `RaiseSideEffects` for **async**
projections. For an **inline** projection it does *nothing* — no
warning, no boot error, no exception. The override exists in your
code, looks correct, compiles, and is silent dead code.

To enable side-effects on inline projections you must opt in
explicitly:

```csharp
options.Events.EnableSideEffectsOnInlineProjections = true;
```

…in `MartenConfiguration.ConfigureEventStore`, alongside the
existing V8-Pins.

Today this flag is **not** set in Modgud because no inline
projection raises side-effects. `UserViewProjection` is async, all
other projections (`PermissionRoleProjection`, `AppProjection`,
`PrincipalProjectionBase` subclasses, `LoginProviderProjection`,
`ExternalIdentityLinkProjection`, the three `OAuth*StateProjection`s)
are inline-but-no-side-effects.

**If you ever override `RaiseSideEffects` on an inline projection**,
flip the flag in the same commit or the side-effect will silently
never fire. Add a unit test that asserts on the dispatched message
to make the regression loud.

## Gotcha 2 — `RaiseSideEffects` does NOT run during projection rebuild

Marten only invokes `RaiseSideEffects` when the projection daemon is
in `ShardExecutionMode.Continuous`. During a **rebuild**
(`docker exec … recover rebuild-projections`, or the admin endpoint
`POST /api/admin/projections/rebuild`) the daemon runs in replay
mode and skips the side-effects entirely.

**Why this matters in Modgud today:** it doesn't, because the
single side-effect we raise is a SignalR push (`UserViewSignalRDispatch`).
SignalR is purely transient — there is no persistent second read-model
to keep in sync, so the worst that happens during a rebuild is "no
live notifications fire to currently-connected clients while the
rebuild runs". Clients reconnecting after the rebuild get the
freshly-rebuilt `UserView` directly from the projection on their next
query.

**When this matters in general:** if a side-effect writes into a
**second persistent read-model** (a denormalised table, an outbox
row consumed by a downstream service, a Wolverine handler that
stamps another document), a rebuild leaves that second model stale.
The mitigation is the same shape every time:

1. Make the second model reconstructible — either re-emit its
   contents from the event stream so a separate projection rebuild
   covers it, or compensate at the end of the primary rebuild with
   an explicit "resync" command per affected document.
2. Wire the compensation into the rebuild path (Recovery-CLI
   `rebuild-projections` and the admin endpoint), not as a
   developer-remembers-to-run-it manual step.

Modgud has no equivalent today because nothing needs it, but the
shape above is the precedent if/when we add a side-effect that
targets a persistent model.

## Where this is enforced

- **Schema-level pin:** `Modgud.Infrastructure/Persistence/Marten/Configuration/MartenConfiguration.cs`
  is the single place where `options.Events` settings live. The two
  flags above (V8 `AppendMode`/`UseIdentityMapForAggregates`,
  `EnableSideEffectsOnInlineProjections`) all belong there.
- **Recovery-CLI:** `Modgud.Authentication/Api/Admin/RecoveryCli.cs`
  `RebuildProjectionsAsync` — extend here if a future projection
  needs a post-rebuild compensation step.
- **Side-effect target audit:** `grep -rn "RaiseSideEffects" src/dotnet/`
  lists every override; check each one against the "what does the
  side-effect persist?" question before approving a PR that adds a
  new one.

## See also

- Marten docs — [Side Effects](https://martendb.io/events/projections/event-projections.html#side-effects)
