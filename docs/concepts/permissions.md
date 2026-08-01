# Permissions & gating

Modgud uses **granular per-resource gating**: every endpoint and every
sidebar item checks a single permission string. The IdP evaluates and
pre-expands grants; resource servers perform exact claim checks through
`Modgud.AspNetCore.ResourceServer`.

## Permission format

**Two segments:** `<resource>:<action>`. The string carries no app slug.

The app context is **implicit from the caller**:

- For in-process gates inside Modgud, the gate's audience is the
  Modgud app itself.
- For resource-server gates, the token audience resolves an OAuth API;
  that API's `AppId` selects the permission catalog. The OAuth API
  Audience and App slug are separate identifiers and need not match.

This is enforced at write-time: catalog entries are validated against
the regex `^[a-z0-9-]+:[a-z0-9-]+$` — exactly two lowercase segments,
hyphens allowed inside a segment, no colon-prefix.

Modgud's own admin surface uses two App slugs internally:

- **`modgud`** — the realm-internal admin surface (users, groups,
  roles, OAuth clients, login providers, etc.). Seeded into every realm.
- **`control-plane`** — the cross-realm admin surface (realm CRUD).
  Seeded **only** into the realm flagged `IsControlPlane = true`.

A consuming SaaS app gets its own App slug and registers its own
catalog of `<resource>:<action>` permissions. The slug is the *implicit
context* — it never appears in the permission string itself.

| Permission (catalog string) | Meaning |
|---|---|
| `user:read` | Read user list/detail |
| `user:write` | Create/edit users |
| `user:admin` | Resource-wide bypass for all user actions |
| `oauth-client:read` | Read OAuth clients |
| `oauth-client:write` | Create/edit OAuth clients |
| `permission-role:read` | Read roles |
| `authorization-group:write` | Create/edit groups |
| `login-provider:read` / `:write` | Login-provider management |
| `auth-log:read` | Read the auth log |
| `gdpr:admin` | Permanent-erase GDPR operations |
| `realm:read` / `realm:write` | Realm CRUD (control-plane app) |
| `realm:admin` | **Realm-wide bypass** (every app, every resource, every action) |

`realm:admin` is the one exception: it's a *realm-constant* — a
PermissionRole carries `IsRealmAdmin = true` and the resolver injects
the literal grant. It bypasses every check anywhere in the realm.

## Bypass tiers

Only **two** tiers, both checked by `PermissionEvaluator`:

| Grant | Effect |
|---|---|
| `realm:admin` | Everything in every app — the realm-wide emergency exit |
| `<resource>:admin` | All actions on that resource in the calling app |

`Evaluate(grants, "user:read")` returns true when:

1. the user holds `realm:admin`, **or**
2. the user holds `user:read` directly, **or**
3. the user holds `user:admin` (resource-wide bypass).

There is **no app-wide bypass tier** (`<app>:admin` was discussed but
never shipped — bypass is either realm-wide or resource-wide, nothing
in between). Per-area owners typically get per-resource `<resource>:admin`
(e.g. an OAuth owner gets `oauth-client:admin` + `oauth-scope:admin`
+ `oauth-api:admin`, but not `user:admin`).

The canonical evaluator implementation lives in
`Modgud.Permissions.Abstractions/PermissionEvaluator.cs` and is used
inside Modgud. At the token boundary Modgud pre-expands bypasses; the
resource-server package performs exact checks against the projected
concrete claims and does not run the evaluator.

## Resources

### `modgud` app catalog (realm-internal — every realm)

| Resource | What for |
|---|---|
| `app` | App registration management |
| `user` | User management (`ApplicationUser`) |
| `permission-role` | Role management |
| `authorization-group` | Group management |
| `session` | Per-user session management |
| `service-account` | Service-account identity layer |
| `auth-log` | Read AuthLog |
| `audit-log` | Read the audit log (admin actions, distinct from AuthLog) |
| `gdpr` | Permanent-erase GDPR operations |
| `oauth` | OAuth admin surface umbrella |
| `oauth-client` | OAuth client management |
| `oauth-scope` | OAuth scope management |
| `oauth-api` | OAuth API resource management |
| `login-provider` | Internal/external login providers |
| `realm-settings` | Per-realm settings (self-reg, DCR, branding) |
| `asset` | Asset library |
| `observability` | Read-only observability view |
| `scheduled-job` | Quartz scheduled job admin |
| `inbox-settings` | Inbox retention configuration |

### `control-plane` app catalog (only on the Control-Plane realm)

| Resource | What for |
|---|---|
| `realm` | Realm CRUD (`/api/admin/realms/*`) |

The `control-plane` slug is intentionally decoupled from the product
name. If the IdP is ever rebranded, cross-realm permissions don't need
a migration.

## How resource servers receive permissions

For each token audience (`aud`) that resolves to a registered OAuth API
linked to an App, Modgud can build a **`resource_access[<audience>]`**
block shaped like Keycloak's nested format. `roles` must be granted to
emit its role array; `permissions` must be granted to emit its
permission array. If neither scope is present, or no audience resolves
to an OAuth API with an App, the whole claim is absent.

For JWT access tokens the claim is carried on the wire. For reference
tokens it remains in the server-side payload and is exposed only through
authorized introspection. `/connect/userinfo` returns the same block for
an eligible bearer token:

```json
{
  "sub": "…",
  "resource_access": {
    "billing-api": {
      "permissions": ["invoice:read", "invoice:write"],
      "roles": ["billing-owner"]
    },
    "shipping-api": {
      "permissions": ["shipment:read"],
      "roles": []
    }
  }
}
```

What's in the block:

- **Bypass-pre-expansion** — `realm:admin` is expanded server-side into
  every concrete catalog string in the audience's linked App; a
  `<r>:admin` bypass is expanded into every `<r>:*` string in that
  App's catalog.
  Consumers do straight exact-match — no PermissionEvaluator port
  required client-side.
- **Per-RS-subset narrowing** — each audience block is narrowed to
  `OAuthApi.PermissionIds` (the catalog subset the resource server
  declared as its gating surface). Anything outside that subset is
  excluded — no permission strings leak from one microservice into a
  sibling's block.
- **Roles vs Permissions** are gated by separate scopes
  (`roles`, `permissions`) — request `scope=permissions` to see the
  permissions array and `scope=roles` to see the role array. Requesting
  only one never implicitly adds the other.

The `Modgud.AspNetCore.ResourceServer` authentication handlers project
the matching audience block onto the principal so standard ASP.NET Core
`[Authorize(Roles="…")]` and `RequireModgudPermission(…)` work out of
the box.

## Backend gating: `RequiresPermission`

Endpoints gate via a `RouteHandlerBuilder` extension:

```csharp
app.MapGet("/api/user", async (...) => { ... })
   .RequiresPermission("user:read");

app.MapPost("/api/user", async (...) => { ... })
   .RequiresPermission("user:write");

app.MapPost("/api/admin/realms", async (...) => { ... })
   .RequiresPermission("realm:write");      // control-plane app context
```

The filter (`PermissionEndpointFilter`):

1. Reads `ClaimTypes.NameIdentifier` from `HttpContext.User`
2. Resolves the user's effective permissions via
   `IPermissionService.GetUserPermissionsAsync(userId, appSlug)`
   (BFS through groups, BoundTo-filtered, role-filtered by AppSlug,
   already bypass-pre-expanded)
3. Calls `PermissionEvaluator.Evaluate(grants, "user:read")`

## Frontend gating: sidebar + buttons

The `auth.store.ts` (Pinia) loads the effective permissions of the
current user at login and uses the same evaluator logic:

```typescript
// grants: string[]  e.g. ["user:read", "user:write"]

function hasPermission(needed: string): boolean {
  const grants = permissions.value
  if (grants.includes('realm:admin')) return true       // tier 1
  if (grants.includes(needed)) return true              // exact match

  const parts = needed.split(':')
  if (parts.length === 2) {                             // tier 2
    if (grants.includes(`${parts[0]}:admin`)) return true
  }
  return false
}
```

Sidebar items in `views/admin/AdminView.vue` declare which permissions
make them visible:

```typescript
const allNavItems: NavItem[] = [
  { section: 'authorization', label: 'nav.users', icon: 'users',
    path: '/admin/users', requirePermissions: ['user:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window',
    path: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
  { section: 'system', label: 'admin.realms.title', icon: 'globe',
    path: '/admin/realms', requirePermissions: ['realm:read'] },
  { section: 'system', label: 'nav.settings', icon: 'settings',
    path: '/platform/settings', requirePermissions: ['realm:admin'] },
  // ...
]
```

Sections are hidden when all their items are filtered out. A user
with only `user:read` sees just the Authorization section with
"Users" — no OAuth, no System.

## Control-Plane separation

Because the `control-plane` App catalog is **only** seeded into the
Control-Plane realm's tenant DB, a tenant realm physically cannot
grant `realm:*` permissions under the control-plane context — the
resource registry in that tenant DB doesn't list the `control-plane`
App, so the backend permission validator rejects the grant.

That's the third of three layers protecting the cross-realm admin
surface. The other two:

1. **`ControlPlaneGateMiddleware`** — runs before authentication.
   Returns 404 on `/api/admin/realms/*` from non-CP hosts. The route is
   discoverable only on the Control-Plane realm.
2. **`RequireControlPlaneFilter`** — per-endpoint filter on the realm
   admin route group. Same 404 behaviour, even if the routing layer
   were misconfigured.

See [Concepts: Control Plane / Data Plane](./control-plane)
for the full defence-in-depth diagram.

## Default roles

The first admin in every realm is created via one of the
[bootstrap paths](../getting-started/first-time-setup). Atomic with the
user creation, three default `PermissionRole`s are seeded (idempotent
— re-bootstrapping doesn't duplicate them):

### System Admin

```
IsRealmAdmin: true
```

The new admin is added to the **Administrators** group with
`BoundTo: ["*"]` (active in every app), and that group carries the
System Admin role. Realm-wide bypass — sees and can do everything in
every app.

### User Manager

```
AppId:        <modgud app id>
PermissionIds:[user:read, user:write,
               session:read, session:write,
               authorization-group:read,
               permission-role:read,
               auth-log:read, audit-log:read]
```

Maintains users + groups + sessions, reads roles + auth log + audit log.

### Viewer

```
AppId:        <modgud app id>
PermissionIds:[user:read,
               authorization-group:read,
               permission-role:read]
```

Read-only auditor.

Admins can adjust these roles or create more — they aren't hard-coded.

## Permission resolution in detail

```
Request with cookie/bearer comes in
  ↓
PermissionEndpointFilter
  ↓
ClaimTypes.NameIdentifier → UserId
needed permission "<resource>:<action>" (2 segments)
app context = the calling app's slug (modgud, control-plane, billing-api, …)
  ↓
IPermissionService.GetUserPermissionsAsync(userId, appSlug)
  ├── BFS through all group memberships (transitive, with visited set)
  ├── filter to groups whose BoundTo contains appSlug or "*"
  ├── for each group: load PermissionRole refs
  ├── filter to roles whose AppId == this app (or IsRealmAdmin = true)
  ├── for each role: resolve PermissionIds → catalog strings
  ├── bypass-pre-expand: realm:admin → all reachable catalog strings;
  │                     <r>:admin → all <r>:* in this app's catalog
  └── Set<string> of fully-expanded permissions
  ↓
PermissionEvaluator.Evaluate(grants, "<resource>:<action>"):
  has "realm:admin"? → ✓
  has the exact permission? → ✓
  has "<resource>:admin"? → ✓
  otherwise → 403
```

Resolution is scoped per request, not cached. That is intentional:
permissions change live (an admin removes a user from a group), and
Modgud is not performance-critical (admin UI traffic, not a hot path).

If that ever changes: an `IMemoryCache` with sliding expiration (e.g.
30 seconds) and cache invalidation on
`GroupMembershipRecomputedEvent` would suffice.
