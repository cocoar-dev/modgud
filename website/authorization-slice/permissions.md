# Permissions & gating

cocoar.auth uses **granular per-resource gating**: every endpoint and
every sidebar item checks a single permission string, mirrored
exactly between backend and frontend.

## Permission format

Three segments: `<app>:<resource>:<action>`. Cocoar.Auth's own admin
surface lives under two app slugs:

- **`cocoar-auth`** — the realm-internal admin surface (users, groups,
  roles, OAuth clients, login providers, etc.). Seeded into every realm.
- **`control-plane`** — the cross-realm admin surface (realm CRUD).
  Seeded **only** into the realm flagged `IsControlPlane = true`.

Consuming SaaS apps get their own app slug (`timetodo`, `knowledge`, …)
and permissions under that slug.

| Permission | Meaning |
|---|---|
| `cocoar-auth:user:read` | Read user list/detail |
| `cocoar-auth:user:write` | Create/edit users |
| `cocoar-auth:user:admin` | Resource-wide bypass for all user actions |
| `cocoar-auth:oauth-client:read` | Read OAuth clients |
| `cocoar-auth:oauth-client:write` | Create/edit OAuth clients |
| `cocoar-auth:permission-role:read` | Read roles |
| `cocoar-auth:authorization-group:write` | Create/edit groups |
| `cocoar-auth:login-provider:read|write` | Login provider management |
| `cocoar-auth:auth-log:read` | Read the auth log |
| `cocoar-auth:gdpr:admin` | Permanent-erase GDPR operations |
| `cocoar-auth:admin` | App-wide bypass for the realm-internal admin surface |
| `control-plane:realm:read` | List realms (Control Plane only) |
| `control-plane:realm:write` | Create/edit/deactivate realms (Control Plane only) |
| `realm:admin` | **Realm-wide bypass** (every app, every resource, every action) |

## Bypass tiers

| Grant | Effect |
|---|---|
| `<app>:<resource>:admin` | All actions on that resource within that app |
| `<app>:admin` | All resources within that app |
| `realm:admin` | Everything in every app — the realm-wide emergency exit |

`hasPermission(needed)` returns true when:

1. the user holds `realm:admin`, **or**
2. the user holds the requested permission directly, **or**
3. the user holds `<app>:admin` for the requested permission's app, **or**
4. the user holds `<app>:<resource>:admin` for the requested
   permission's app + resource.

The `realm:admin` bypass is intentionally narrow — only the System
Admin default role carries it. Per-area owners typically get
per-resource `<resource>:admin` (e.g. OAuth owners get
`cocoar-auth:oauth-client:admin` + `cocoar-auth:oauth-scope:admin` +
`cocoar-auth:oauth-api:admin`, but not `cocoar-auth:user:admin`).

## Resources

### `cocoar-auth` (realm-internal — every realm)

| Resource | What for |
|---|---|
| `app` | App registration management |
| `user` | User management (`ApplicationUser`) |
| `role` (`permission-role`) | Role management |
| `authorization-group` | Group management |
| `permission-role` | Role-to-permission management |
| `session` | Per-user session management |
| `auth-log` | Read AuthLog |
| `gdpr` | Permanent-erase GDPR operations |
| `oauth` | OAuth admin surface umbrella |
| `oauth-client` | OAuth client management |
| `oauth-scope` | OAuth scope management |
| `oauth-api` | OAuth API resource management |
| `login-provider` | Internal/external login providers |

### `control-plane` (Control Plane only)

| Resource | What for |
|---|---|
| `realm` | Realm CRUD (`/api/admin/realms/*`) |

The `control-plane` app slug is intentionally decoupled from the product
name. If the IdP is ever rebranded, cross-realm permissions don't need a
migration.

## Backend gating: `RequiresPermission`

Endpoints gate via an `EndpointFilter` extension:

```csharp
app.MapGet("/api/admin/users", async (...) => { ... })
   .RequiresPermission("cocoar-auth:user:read");

app.MapPost("/api/admin/users", async (...) => { ... })
   .RequiresPermission("cocoar-auth:user:write");

app.MapPost("/api/admin/realms", async (...) => { ... })
   .RequiresPermission("control-plane:realm:write");
```

The filter (`PermissionEndpointFilter`):

1. Reads `ClaimTypes.NameIdentifier` from `HttpContext.User`
2. Loads the user's effective permissions via
   `IPermissionService.GetUserPermissionsAsync(userId, appSlug)`
   (BFS through groups, BoundTo-filtered, role-filtered by AppSlug)
3. Runs the bypass cascade above

## Frontend gating: sidebar + buttons

The `auth.store.ts` (Pinia) loads the effective permissions of the
current user at login and mirrors the backend `PermissionEvaluator`:

```typescript
// permissions: string[]  e.g. ["cocoar-auth:user:read", "cocoar-auth:user:write"]

function hasPermission(permission: string): boolean {
  const grants = permissions.value
  if (grants.includes('realm:admin')) return true
  if (grants.includes(permission)) return true

  const parts = permission.split(':')
  if (parts.length === 3) {
    if (grants.includes(`${parts[0]}:admin`)) return true
    if (grants.includes(`${parts[0]}:${parts[1]}:admin`)) return true
  }
  return false
}
```

Sidebar items in `views/admin/AdminView.vue` declare which permissions
make them visible:

```typescript
const allNavItems: NavItem[] = [
  { section: 'authorization', label: 'nav.users',  icon: 'users',
    path: '/admin/users',  requirePermissions: ['cocoar-auth:user:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window',
    path: '/admin/oauth/clients', requirePermissions: ['cocoar-auth:oauth-client:read'] },
  { section: 'system', label: 'admin.realms.title', icon: 'globe',
    path: '/admin/realms', requirePermissions: ['control-plane:realm:read'] },
  { section: 'system', label: 'nav.settings', icon: 'settings',
    path: '/admin/settings', requirePermissions: ['realm:admin'] },
  // ...
]

function canSee(item: NavItem): boolean {
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}
```

Sections are hidden when all their items are filtered out. A user
with only `cocoar-auth:user:read` sees just the Authorization section
with "Users" — no OAuth, no System.

## Control-Plane separation

Because the `control-plane` app slug is **only** seeded into the
Control-Plane realm's tenant DB, a tenant realm physically cannot grant
`control-plane:realm:*` permissions — the resource registry in that
tenant DB doesn't list the `control-plane` app, so the backend
permission validator rejects the grant.

That's the third of three layers protecting the cross-realm admin
surface. The other two:

1. **`ControlPlaneGateMiddleware`** — runs before authentication. Returns
   404 on `/api/admin/realms/*` from non-CP hosts. The route is
   discoverable only on the Control-Plane realm.
2. **`RequireControlPlaneFilter`** — per-endpoint filter on the realm
   admin route group. Same 404 behaviour, even if the routing layer were
   misconfigured.

See [Concepts: Control Plane / Data Plane](../concepts/control-plane) for
the full defence-in-depth diagram.

## Default roles

The first admin in every realm is created via one of the [bootstrap paths](../getting-started/first-time-setup). Atomic with the user creation, three default `PermissionRole`s are seeded (idempotent — re-bootstrapping doesn't duplicate them):

### System Admin
```
permissions: ["realm:admin"]
```
The new admin is added to the **Administratoren** group with
`BoundTo: ["*"]` (active in every app), and that group carries the System
Admin role. Realm-wide bypass — sees and can do everything in every app.

### User Manager
```
permissions: [
  "cocoar-auth:user:read", "cocoar-auth:user:write",
  "cocoar-auth:session:read", "cocoar-auth:session:write",
  "cocoar-auth:authorization-group:read",
  "cocoar-auth:permission-role:read",
  "cocoar-auth:auth-log:read"
]
```
Maintains users + groups + sessions, reads roles + auth log.

### Viewer
```
permissions: [
  "cocoar-auth:user:read",
  "cocoar-auth:authorization-group:read",
  "cocoar-auth:permission-role:read"
]
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
needed permission → split into (appSlug, resource, action)
  ↓
IPermissionService.GetUserPermissionsAsync(userId, appSlug)
  ├── BFS through all group memberships (transitive, with visited set)
  ├── filter to groups whose BoundTo contains appSlug or "*"
  ├── for each group: load PermissionRole refs
  ├── filter to roles whose AppSlug == appSlug
  ├── for each role: expand actions to "<app>:<resource>:<action>"
  └── Set<string> of fully-qualified permissions
  ↓
Checks:
  has "realm:admin"? → ✓
  has needed permission directly? → ✓
  has "<app>:admin"? → ✓
  has "<app>:<resource>:admin"? → ✓
  otherwise → 403
```

Resolution is scoped per request, not cached. That is intentional:
permissions change live (an admin removes a user from a group), and
cocoar.auth is not performance-critical (admin UI traffic, not a hot
path).

If that ever changes: an `IMemoryCache` with sliding expiration (e.g.
30 seconds) and cache invalidation on
`GroupMembershipRecomputedEvent` would suffice.
