# Plan — Applications & App-scoped Permissions (Phase 1)

> Internal plan, not user docs. Companion to STATUS.md/backlog.md.
> Captures the design discussion of 2026-04-29 and turns it into a
> concrete, ordered first step.

## Goal

Make Cocoar.Auth ready to act as a central IAM for multiple Cocoar SaaS
apps. Phase 1 introduces the **Application** aggregate and **app-scoped
permissions** internally — Cocoar.Auth itself is registered as the first
application (`cocoar-auth`) and its existing permissions are migrated
into that namespace. **No external app is integrated yet.**

The model is validated by dogfooding: if Cocoar.Auth runs correctly
under the new scheme, the foundation for a second app is solid.

## What we decided (recap)

- Each realm has one or more **Applications** (`Application` aggregate).
  An Application owns its own resources and the roles defined on them.
- **Roles are app-scoped** — a role's permissions reference resources of
  exactly one app. No explicit `Role.BoundTo` field needed; the app is
  implicit in the role's resources.
- **Groups stay realm-wide** but get a `BoundTo: string[]` field listing
  the app slugs in which the group is *active*. Empty/missing means the
  group is dormant for permission purposes (purely organisational, e.g.
  distribution lists).
- **No `Manager` field on groups** for now. MVP authority model:
  - `realm:admin` → full bypass within a realm
  - `<app>:admin` → full bypass within a given app
  - Granular per-group ownership comes later, only if needed.
- **Apps are per realm** (not global). Realm A and Realm B can both have
  an app `timetodo`, but they are independently registered and isolated.
- **No migration burden** — there is no production data and no
  staging environment. We rebuild internals freely.

## Out of scope for Phase 1 (deferred)

- App-onboarding UI and API (apps registered programmatically in the
  seeder for now; UI/API in Phase 2)
- Token-content changes — what scopes/claims a JWT carries
- External-app **permission distribution API** (the endpoint TimeToDo
  will eventually call)
- SignalR-based permission invalidation
- OAuth client ↔ Application linking (`n:m` was decided, but the link
  table can wait until a second app exists)

## Design choices that need a one-line confirmation before coding

1. **Permission-string format:** `<app>:<resource>:<action>`, e.g.
   `cocoar-auth:user:read`, `timetodo:todo:write`.
2. **Rename existing `app:admin` → `realm:admin`** (the current global
   bypass). This avoids semantic collision with `<app>:admin`. All ~40
   `[RequiresPermission("app:admin")]` sites change.
3. **Introduce `<app>:admin` as an app-wide bypass**, evaluated in
   `PermissionEvaluator` analogously to the today's `app:admin` bypass.
4. **Application is a discriminator within a realm**, not an
   isolation boundary. Realm = hard tenant boundary (separate Marten
   store, separate DB). Application = logical scope inside that store
   (column on existing tables). `Application` documents live in the
   per-realm tenant DB, not the master DB.
5. **Open question, deferred but worth flagging:** the existing
   `OAuthApiAggregate` (under `Domain/OAuth/Apis/`) represents the
   OAuth resource-server concept. The new `Application` aggregate is
   conceptually adjacent but distinct (Application owns
   resources/roles, OAuthApi owns scopes/audiences). For Phase 1 they
   stay separate; we revisit the relationship when an external app
   actually requests tokens.

## Data-model changes

### New aggregate: `Application`

Per-realm document, lives in `Cocoar.Auth.Authorization` next to
`PermissionRole`. **Application is a logical discriminator within the
realm, not an isolation boundary** — the realm/tenant split already
provides hard isolation via the per-realm Marten store. The app axis
is just a column-level filter on existing tenant-scoped tables.

```
Application {
    Slug:        string   // immutable, e.g. "cocoar-auth", "timetodo"
    DisplayName: string
    Description: string?
    Resources:   string[] // e.g. ["user", "oauth-client", "session", ...]
    IsSystem:    bool     // true for cocoar-auth (cannot be deleted)
}
```

Event-sourced (matches the existing pattern of OAuth aggregates) with
events `ApplicationCreated`, `ApplicationDisplayNameChanged`,
`ApplicationResourceAdded`, `ApplicationResourceRemoved`,
`ApplicationDeleted`.

Implications of "discriminator, not boundary":

- No app-scoped Marten session, no per-app DB schema. `Application`,
  `PermissionRole`, `Group`, `ResourceRegistry` all live in the same
  per-realm store.
- Filtering by app is a `WHERE application_slug = @app` query
  predicate, not a session-level switch.
- A single `Application` document type per realm is fine — no need
  for per-app document collections. Lookup is by slug.

### Modified: `PermissionRole`

Add `ApplicationSlug: string` (required, validated against existing
Application). Keep `ResourceType` and `Permissions` shape — the change
is additive. Permission resolution from a role becomes
`{ApplicationSlug}:{ResourceType}:{action}`.

### Modified: `Group` (in `Principals/Group.cs`)

Add `BoundTo: string[]` (list of app slugs). Empty means group is
inactive for all permission purposes — only useful as a distribution
list. No cascade on add/remove (per discussion: BoundTo is an
activation switch, removing an app does **not** strip roles).

### Modified: `ResourceRegistry`

Becomes app-aware. `RegisterResource(...)` calls in
`Cocoar.Auth.Infrastructure/DependencyInjection.cs` get an extra
`appSlug` argument. The registry stores `(appSlug, resource) → actions`
and validates permissions against that compound key.

For Phase 1 the registrations are still hardcoded — every current
resource is registered under `appSlug = "cocoar-auth"`.

### Modified: `PermissionService`

```csharp
// before
Task<bool> HasPermissionAsync(Guid userId, string permission, ...)
Task<IReadOnlySet<string>> GetUserPermissionsAsync(Guid userId, ...)

// after
Task<bool> HasPermissionAsync(Guid userId, string appSlug, string permission, ...)
Task<IReadOnlySet<string>> GetUserPermissionsAsync(Guid userId, string appSlug, ...)
```

Resolution algorithm:

```
1. Load all groups, build parent map (unchanged).
2. BFS from userId → set of group IDs (unchanged).
3. Filter groups: keep only those where appSlug ∈ Group.BoundTo.
4. Load PermissionRoles for collected RoleIds.
5. Filter roles: keep only those where Role.ApplicationSlug == appSlug.
6. Expand permissions to "<app>:<resource>:<action>".
7. realm:admin and <app>:admin (matching app) → unrestricted bypass.
```

### Modified: `PermissionEndpointFilter` & `RequiresPermission`

The endpoint filter needs to know **which app** an endpoint belongs to.
For Phase 1 every Cocoar.Auth endpoint is in the `cocoar-auth` app —
the simplest implementation is a hardcoded constant in the filter (or
an additional optional argument that defaults to `"cocoar-auth"`).
External apps in Phase 2 will call the distribution API with their own
slug.

All call sites of `[RequiresPermission("foo:bar")]` get rewritten to
`[RequiresPermission("cocoar-auth:foo:bar")]`. The `app:admin` bypass
literal becomes `realm:admin`.

## Bootstrap / seeding

### Realm provisioning (`RealmProvisioningService.CreateRealmAsync`)

After existing scope/login-provider seeding, add:

1. Create `Application` document with slug `cocoar-auth`, the system
   Resources list, `IsSystem=true`.
2. (No automatic admin group here — that happens in `/setup`.)

### First-time setup (`SetupEndpoints` POST `/setup/create-admin`)

Existing flow already creates a "System Admin" `PermissionRole` and an
"Administratoren" `Group`. Adjust:

- The seeded role gets `ApplicationSlug = "cocoar-auth"` and its
  permission list becomes `["realm:admin"]` (instead of today's
  `["admin"]` which expanded to `app:admin`).
- The seeded group gets `BoundTo = ["cocoar-auth"]`.
- The two starter roles ("User Manager", "Viewer") similarly get
  `ApplicationSlug = "cocoar-auth"`.

## Implementation order

Each step is small enough to commit individually and have green tests
at the boundary. Stop and review between steps if anything feels wrong.

1. **Add `Application` aggregate** (events, projection, read model,
   Marten registration). No callers yet. Tests for aggregate creation /
   resource add/remove.
2. **Seed `cocoar-auth` Application** in `RealmProvisioningService`.
   Verify by spinning up a fresh realm in the integration tests.
3. **Add `ApplicationSlug` to `PermissionRole`** (additive, default
   `"cocoar-auth"` during the rebuild). Update projection. Update
   `SetupEndpoints` to set the slug explicitly on seeded roles.
4. **Add `BoundTo: string[]` to `Group`**. Update projection. Default
   `["cocoar-auth"]` in `SetupEndpoints` for the Administratoren group.
   Existing `CreateGroupCommand` / `UpdateGroupCommand` get an extra
   field.
5. **Refactor `ResourceRegistry`** to be `(appSlug, resource)`-keyed.
   Pass `cocoar-auth` everywhere in
   `Cocoar.Auth.Infrastructure/DependencyInjection.cs`.
6. **Refactor `PermissionService`** signatures (add `appSlug`). Apply
   the new resolution algorithm. Update `PermissionEvaluator` to
   recognise `realm:admin` and `<app>:admin` as bypasses.
7. **Refactor `PermissionEndpointFilter`** + `RequiresPermission`
   extension methods to thread `appSlug` (default `"cocoar-auth"` for
   Phase 1).
8. **Bulk-rewrite `[RequiresPermission(...)]` literals** —
   `app:admin` → `realm:admin`, all others gain `cocoar-auth:` prefix.
9. **Run the full test suite, fix what falls over.**

## Test impact (rough estimate)

- **Unit tests** (~757 today): the `PermissionService`,
  `PermissionEvaluator`, and `ResourceRegistry` test files all need
  updates — call sites change. Likely 50-100 individual test bodies
  touched, but each change is mechanical.
- **Integration tests** (89/96 green today): every test that hits a
  permission-gated endpoint and asserts on 403 vs 200 will need its
  permission seed updated. The shared `IntegrationTestBase` likely
  centralises permission seeding — most updates land there.
- New tests to add:
  - `Application` aggregate behaviour (creation, resource list,
    `IsSystem` immutability)
  - `Group.BoundTo` permission filter (group with `BoundTo=[]` yields
    no permissions; group with mismatched `BoundTo` is filtered out)
  - `realm:admin` and `<app>:admin` bypass behaviour
  - `PermissionService` with multiple apps in the registry (add a
    fake second app to validate filtering really works — even though
    Phase 1 only ships `cocoar-auth` to production)

## What this enables for Phase 2

Once Phase 1 lands, adding a second app becomes a small concrete task
rather than an open design question:

- `Application` document for the new app
- `ResourceRegistry` registrations under that slug
- A seeded `<app>-admins` group with `BoundTo=[app-slug]` and a role
  with `ApplicationSlug=app-slug, Permissions=["admin"]`
- An external endpoint (Phase 2 distribution API) that the app calls
  with its slug to retrieve permissions for a user

The token-content question and the distribution-API question can be
answered once with the second app's actual integration as the test —
not speculatively today.
