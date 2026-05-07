# Application as the permission catalog; Resource Server gets a subset

> **Status:** Idea — captured 2026-05-07. Not started.
> **Why:** While walking a user through OAuth-API setup we hit a
> conceptual cliff: the IdP's `App.Resources` field stores only the
> middle segment of a permission (`policy`, `knowledge`, `mcp`),
> while the *actions* (`read`, `write`, …) live in code via
> `opt.RegisterResource("cocoar-policy", "policy", "read", "write")`.
> User instinct (correctly): "couldn't I just type `mcp:read,
> mcp:write` directly? And the **App** should know **ALL**
> permissions; a **Resource Server** gets the valid subset assigned
> to it." Yes — that's the cleaner architecture.

## The refined model

```
App  ─── full permission catalog (the canonical list of what gating
 │       this product knows about)
 │
 ├── Resource Servers (OAuth APIs)
 │     each one gets a SUBSET of App.Permissions assigned —
 │     "these are the permissions I serve / gate on my surface"
 │
 └── OAuth Clients
       can request scopes; scopes resolve to permissions out of
       the assigned-to-this-resource-server subset
```

**One App, possibly several Resource Servers.** A bigger product
can split its surface — e.g. cocoar-policy might have a
`policy-api` (gates `policy:*`) plus a separate `knowledge-api`
(gates `knowledge:*`) plus an `mcp-api` (gates `mcp:*`). Each
Resource Server only declares the permissions it actually
enforces; the App is the umbrella catalog.

## Resource-server code stays prefix-free

A second design point landed during the same conversation: **the
Resource Server's own code must NOT have to write the App slug.**

The author of the Resource Server doesn't know the slug at code-
time — that's a deployment decision. Two different deployments of
the same RS code can land under different App slugs (white-label
re-skinning, multi-tenant carve-outs, dev/staging/prod with
different naming). Hardcoding `RequiresPermission("cocoar-policy:policy:write")`
in the RS code couples it to a slug that doesn't belong there.

The Resource Server should write:

```csharp
app.MapPost("/api/policy", ...).RequiresPermission("policy:write");
```

…and the middleware (in `Cocoar.Auth.Client.AspNetCore`) prefixes
the configured AppSlug before checking against the user's grants.
Same pattern Keycloak uses: `resource_access[<my-app>].roles`
gets flattened into bare `[Authorize(Roles = "...")]` on the RS
side, with the app context coming from configuration, not code.

### What's there today vs missing

| Concern | Today | Missing |
|---|---|---|
| Role flattening | ✅ `CocoarAuthClaimsTransformation` reads `resource_access[appSlug].roles` and surfaces flat `ClaimTypes.Role` | – |
| Permission flattening | ❌ no permission claim is emitted into tokens (cf. the TODO in `AuthorizationEndpoints.CreateClaimsPrincipalAsync`) | a parallel `resource_access[appSlug].permissions` claim |
| Client-side `RequiresPermission(bare)` | ❌ — the existing `RequiresPermission` extension lives in `Cocoar.Auth.Authorization` and is hardcoded to `AppSlugs.CocoarAuth` (= the IdP itself) | a Cocoar.Auth.Client.AspNetCore equivalent that prefixes with the configured `AppSlug` |
| RS-side validation against the App's catalog | ❌ — the RS doesn't know what permissions exist for its app | distribution-API endpoint that returns `App.Permissions` filtered to the RS's subset, fetched at startup |

### Implementation outline

1. **Token issuance**: extend `CreateClaimsPrincipalAsync` in
   `AuthorizationEndpoints` to populate
   `resource_access[<app-slug>].permissions = [...]` from the
   user's effective permission grants, filtered to the
   audience-bound Resource Server's subset. The TODO at line
   677-678 hints this is already on the roadmap.

2. **Client-side extension**: ship
   `Cocoar.Auth.Client.AspNetCore.PermissionEndpointFilter` that:
   - Reads `CocoarAuthOptions.AppSlug` from DI
   - Reads `resource_access[<AppSlug>].permissions` from the
     `ClaimsPrincipal`
   - Evaluates `<configured-AppSlug>:<bare-permission>` against
     the user's grants using the same `PermissionEvaluator`
     wildcard logic the IdP uses
   - Returns 401/403 like the IdP-side filter

3. **Distribution API**: new endpoint
   `GET /api/v1/distribution/me-app-catalog` returning the
   calling RS's full assigned permission subset. RS calls this at
   startup (or on-demand) to populate its own validator and to
   power the role-creation Dropdown for tenant admins.

4. **Cross-deployment slug independence**: the RS's `Program.cs`
   reads `AppSlug` from configuration, not from a constant:
   ```csharp
   services.AddCocoarAuthClaimsTransformation(o =>
       o.AppSlug = builder.Configuration["Cocoar:AppSlug"] ?? "cocoar-policy");
   ```
   Same code, two deployments, two different slugs — works.

## The current split

| Source | What it stores | Used for |
|---|---|---|
| `App.Resources` (DB) | Just resource **names** (`policy, knowledge, mcp`) | Admin-UI hint + validation metadata |
| `IResourceRegistry` (in-memory) | (App, Resource) → set of actions | Validation when admins create roles |
| `PermissionEvaluator` (runtime) | — | **Pure string match** + 3 wildcard bypasses |

The runtime gate doesn't consult either of the first two — it
just checks if the user's granted permission set contains the
required string (or one of the wildcard bypasses
`realm:admin` / `<app>:admin` / `<app>:<resource>:admin`).

So the registry exists to **validate role-creation** ("you can't
grant `cocoar-policy:policy:flerp` because flerp isn't a known
action"), and the App.Resources field exists to **document** what
resources an app has. The actions are knowable in only one place:
the app's own startup code.

## Why this is awkward

- **Cocoar.Auth admin can't see what permissions are valid for an
  external app.** When the realm admin creates a role and wants to
  grant `cocoar-policy:policy:write`, the IdP's role-editor has no
  way to suggest valid actions — it only knows about its own +
  control-plane registries (which are populated from `cocoar.auth`'s
  startup code, in the same process).
- **Two sources of truth** that have to be kept in sync manually.
  Nothing alerts you when `App.Resources` says `policy` exists but
  no `RegisterResource` call backs it.
- **Wrong mental model.** A user creating an app naturally thinks
  in terms of "what permissions does my app gate?" — i.e., full
  `resource:action` strings. Splitting that into "name in IdP" +
  "actions in code" is non-obvious.

## Proposed redesign

### Rename + reshape the App field

`App.Resources` (today: `List<string>` of bare resource names) is
renamed and reshaped to `App.Permissions` — `List<string>` of
full permission strings:

```
policy:read
policy:write
policy:approve
knowledge:read
knowledge:write
mcp:read
mcp:write
```

Or as a structured representation: `List<AppPermission>` where each
entry is `(Resource: "policy", Action: "read", Description?: "...")`.
Storage shape is an implementation detail — the user-facing form is
the explicit permission strings.

This is **the Application's complete catalog**. Every permission the
app's gating logic might check goes here. Adding a new gate in code
is a two-step change: write the gate, add the permission to the
App's catalog.

### Resource Server picks a subset

Each `OAuthApi` (Resource Server) gains a `Permissions: List<string>`
field containing a subset of its parent App's `Permissions`.

- "this Resource Server is responsible for serving these
  permissions" — declarative.
- The admin UI shows the App's catalog as a checklist; the operator
  ticks the ones this RS handles.
- Two Resource Servers under the same App can have overlapping or
  disjoint subsets — both is valid (overlap = redundant gating
  layer; disjoint = clean surface split).

### Where validation kicks in

- **Role editor**: Dropdown is the union of all Apps' catalogs
  (filtered by the realm admin's reach — if granting against
  `cocoar-policy`, only that app's permissions show).
- **Token issuance**: when issuing an access token bound to a
  specific Resource Server (RFC 8707 audience), only permissions in
  that RS's assigned subset are eligible to land in the token. A
  user with `cocoar-policy:knowledge:write` requesting a token for
  `policy-api` doesn't get that claim through — `policy-api` doesn't
  serve knowledge writes.
- **Runtime gate (in the RS itself)**: stays a pure string match
  with bypass shortcuts. The constraints above mean only legitimate
  permissions reach the gate, but the gate doesn't care — it's just
  string equality.

### Implications

1. **`App.Permissions` becomes the single source of truth** for what
   permissions an app declares. The admin UI's role-editor reads it
   directly — instant Dropdown of `cocoar-policy:policy:read`,
   `cocoar-policy:policy:write`, etc.

2. **`opt.RegisterResource()` becomes redundant** for external apps.
   The IdP's runtime registry is populated from the DB on startup,
   merged with the IdP's own code-time registrations.

3. **External apps (cocoar-policy) declare permissions in the IdP,
   not in their own code.** Their code still gates endpoints with
   the strings, but the *registration* moves to the IdP's UI/DB.
   The app can fetch its declared permission set at startup via the
   distribution API to populate its own validator.

4. **System app (cocoar-auth) keeps its code-time registrations** —
   it's the IdP itself, has direct DI access to the registry, and
   doesn't need DB-driven configuration.

5. **Token-audience binding actually means something.** A token
   `aud=policy-api` carries only `policy:*` permissions, never
   `knowledge:*`. RFC 8707 stops being just an `aud` label and
   starts gating the actual claim payload.

## Breaking changes

- **Schema migration**: existing `App.Resources: List<string>`
  entries (containing bare names like `app, user, role`) need to
  expand into full permission strings. Could be done by running a
  one-time migration that reads the in-process ResourceRegistry
  for `cocoar-auth` + `control-plane` and rewrites their stored
  resources accordingly. External apps with bare names get a manual
  intervention prompt in the admin UI.
- **`opt.RegisterResource()` API**: stays for `cocoar-auth` itself,
  marked obsolete for external apps with a hint pointing at the
  admin UI.
- **Distribution API extension**: new endpoint
  `/api/v1/distribution/permissions` that returns the calling RS's
  declared permission list. cocoar-policy fetches this at startup
  to populate its own gate-validator (or just hardcodes them in
  code with a note "must match IdP's App.Resources").

## Effort estimate

- DB schema + migration: **1 day**
- Admin UI form changes (Apps + Roles + Permission picker): **2 days**
- Distribution-API extension: **0.5 day**
- Update cocoar-auth's own seed to use the new shape: **0.5 day**
- Cross-app docs + walkthrough updates: **0.5 day**
- Tests (existing ones probably break, plus new coverage): **1 day**

**Total: ~1 week**.

Backwards-compat with bare-name `App.Resources` entries: support
both formats during transition (entries without `:` are treated as
"name only, actions implied via registry"; entries with `:` are
explicit). After the transition, remove the bare-name fallback in
the next release.

## What this enables that's currently missing

1. **Cross-app permission picker in the role editor.** Today the
   admin can grant `realm:admin` from a dropdown but has to type
   `cocoar-policy:policy:write` as free text. After: full Dropdown
   per app/resource/action.

2. **Audit surface — "what does this app gate?".** A realm admin
   inspecting a third-party app can see the full gating list in
   one place instead of having to read the app's source.

3. **Validation at role-creation time.** Currently the registry
   only validates `cocoar-auth`'s own permissions; a typo in a
   role like `cocoar-policy:polcy:write` (note: `polcy`) silently
   passes. After: rejected at role save unless the permission
   string is in the app's declared list.

4. **Cleaner story for the SaaS App Integration Walkthrough.** The
   "register your app's permissions" step becomes a single admin-UI
   form instead of an admin-UI form + a code call.

## Sequencing

Not before the MCP integration ships (cocoar-policy as the first
real-world consumer). Once we have one external-app integration in
production, we'll have concrete sense of the role-editor pain. Then
this refactor.

If a customer onboards an app and complains about typing free-text
permission strings → priority bumps. Otherwise sits as polish.

## Workaround until then

Users *can* type full permission strings into the current
`App.Resources` field (it's `List<string>` with no validation
beyond non-empty/distinct). The system works because the runtime
gate is pure string-match — `cocoar-policy:policy:write` resolves
correctly whether the field stored `policy` or `policy:write`.

So a pragmatic interim: tell users to **write the full strings**.
The field's UI hint should change accordingly (current label says
"Resources (eine pro Zeile)" — should become something like
"Permissions (one per line, format `resource:action`)").

That's a cheap UX-only change that doesn't require the schema
refactor; doing it would prepare the ground for the bigger move.
