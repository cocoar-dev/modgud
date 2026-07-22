# Feature Flags

Operator-level toggles for features that aren't ready for general exposure yet. Lives in `AppSettings.Features` — **not** per-tenant. The operator (whoever sets the deployment's configuration) decides; realm admins can't override.

::: info Why operator-level
Some features ship with the editor side functional but the runtime side still missing, or with a beta integration where we want to gather real feedback before exposing it. The flag keeps the surface invisible to tenant admins until the operator is comfortable; once flipped, the feature behaves as documented on its own page.
:::

## Setting flags

Configure via `configuration.local.json` (gitignored) or an environment variable. Env-var binding is **case-insensitive** — see [the convention note below](#env-var-casing).

```jsonc
// configuration.local.json
{
  "AppSettings": {
    "Features": {
      "PageBuilder": true
    }
  }
}
```

```bash
# or via env (double-underscore as section separator; casing is not significant):
AppSettings__Features__PageBuilder=true
```

Flags are read at startup; no hot-reload. A flip requires a restart.

## Current flags

| Flag | Default | Effect |
| --- | --- | --- |
| `PageBuilder` | `false` | End-to-end [custom authentication pages](../platform/pages). While off: editor routes are hidden/redirected, Realm/Application page endpoints return 404, DTO/app-info schemas are masked, and auth routes use fixed screens. While on: Realm defaults and Application overrides are editable and rendered on login, forgot-password, and signed-out routes. |

## Defense in depth

For each gated feature the flag fires at every layer the surface touches:

1. **SPA sidebar** — the navigation entry is hidden via `requireFeature` in `AdminView.vue`.
2. **Vue-router** — `beforeEnter` guards on the gated routes redirect to a visible sibling so deep-links don't dead-end on a blank screen.
3. **Backend endpoints** — return **404 Not Found** (not 403 / 401) so curl-callers see "no such endpoint", not "permission denied".
4. **DTO masking** — aggregate and anonymous surfaces (`GET /api/admin/realm-settings`, `GET /api/app-info`) emit the gated section as empty so the SPA can't fingerprint stored data.
5. **Runtime fallback** — auth routes render the fixed screen whenever the flag is off, a schema is absent/invalid, or `?safemode=1` is present.

The stored data itself persists across flips — turning a flag off doesn't delete anything from the tenant DB. Flipping it back on surfaces the existing data unchanged.

## ENV-var casing

Cocoar.Configuration v6 (the binding layer Modgud uses) binds environment variables **case-insensitively**. The section/property names need not match the JSON or C# casing — `AppSettings__Features__PageBuilder=true` and `APPSETTINGS__FEATURES__PAGEBUILDER=true` bind to the same flag. Two underscores (`__`) are the section separator; a single underscore is literal. PascalCase is a readability convention only, not a correctness requirement.

This applies to every config-bound type, not just feature flags. The same rule covers `DbSettings__ConnectionString`, `Observability__Prometheus__BearerToken`, etc.

## Adding a flag (developer note)

For repository contributors — flags follow a deliberate pattern:

1. Add a property to `Modgud.Api.FeatureFlags` with a `false` default.
2. The `IFeatureFlags` abstraction in `Modgud.Authentication` mirrors the property (read-only) so the Authentication slice can gate surfaces without depending on the Api project.
3. Wire it everywhere: sidebar `requireFeature` (with a matching `'PageBuilder' | …` union member in `NavItem`), Vue-router `beforeEnter` guard, backend endpoint 404 short-circuit, DTO masking.
4. Add tests in `Modgud.Api.Tests/Authorization/` covering on/off paths.
5. Document the flag in this file plus its feature page.

Don't add a flag for "I'm not sure if I want this enabled" — flags are commitment-eating maintenance work. Add them only when there's a concrete reason the feature isn't ready for general exposure (beta integration, half-built runtime, customer-specific).
