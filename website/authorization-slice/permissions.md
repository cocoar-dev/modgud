# Permissions & gating

cocoar.auth uses **granular per-resource gating**: every endpoint and
every sidebar item checks a single permission string, mirrored
exactly between backend and frontend.

## Permission format

Three segments: `<app>:<resource>:<action>`. Cocoar.Auth's own admin
surface lives under the system app `cocoar-auth`; consuming SaaS apps
get their own app slug (`timetodo`, `knowledge`, …) and permissions
under that slug.

| Permission | Meaning |
|---|---|
| `cocoar-auth:user:read` | Read user list/detail |
| `cocoar-auth:user:write` | Create/edit users |
| `cocoar-auth:user:delete` | Delete users (soft + GDPR) |
| `cocoar-auth:user:admin` | Resource-wide bypass for all user actions |
| `cocoar-auth:oauth-client:read` | Read OAuth clients |
| `cocoar-auth:oauth-client:write` | Create/edit OAuth clients |
| `cocoar-auth:oauth-client:delete` | Delete OAuth clients |
| `cocoar-auth:permission-role:read` | Read roles |
| `cocoar-auth:authorization-group:write` | Create/edit groups |
| `cocoar-auth:realm:write` | Create/edit realms |
| `cocoar-auth:admin` | App-wide bypass for the IAM admin surface |
| `realm:admin` | **Realm-wide bypass** (every app, every resource, every action) |

The permission `app:admin` does not exist as a real grant — `app` is
not a reserved app slug. The three bypass tiers are documented below.

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

## Resources in cocoar.auth

| Resource | What for |
|---|---|
| `user` | User management (Cocoar.Auth.Authentication.ApplicationUser) |
| `permission-role` | Role management |
| `authorization-group` | Group management |
| `oauth-client` | OAuth client management |
| `oauth-scope` | OAuth scope management |
| `oauth-api` | OAuth API resource management |
| `login-provider` | Internal/external login providers |
| `idp-config` | OIDC IdP configurations |
| `realm` | Realm CRUD (only in realms with `CanManageTenants = true`) |
| `auth-log` | Read AuthLog |
| `app` | App admin surface |

Registered at boot, keyed by `(appSlug, resource)`:

```csharp
// AddInfrastructure → AddCocoarAuthAuthorization(opts => { ... })
opts.RegisterResource("cocoar-auth", "user");
opts.RegisterResource("cocoar-auth", "permission-role");
opts.RegisterResource("cocoar-auth", "authorization-group");
// ...
```

## Backend gating: `RequiresPermission`

Endpoints gate via an `EndpointFilter` extension:

```csharp
app.MapGet("/api/admin/users", async (...) => { ... })
   .RequiresPermission("cocoar-auth:user:read");

app.MapPost("/api/admin/users", async (...) => { ... })
   .RequiresPermission("cocoar-auth:user:write");

app.MapDelete("/api/admin/users/{id}", async (...) => { ... })
   .RequiresPermission("cocoar-auth:user:delete");

app.MapDelete("/me", async (...) => { ... })
   .RequiresPermission("realm:admin");
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

## Per-realm domain extension

When a realm has `CanManageTenants = true` (in cocoar.auth, only the
system realm), its users additionally get access to the `realm`
resource. Users of every other realm can't see the realm list at all
— `cocoar-auth:realm:read` is not available in their
`permission-role`s.

The frontend handles that via visibility; the backend is stricter:
`RealmsEndpoints` checks both `cocoar-auth:realm:read` /
`cocoar-auth:realm:write` **and** that the current realm has
`CanManageTenants = true`. Otherwise 404 (not 403, because the
existence of realm CRUD must not be leaked).

## Default roles

First-time setup creates three default roles (once per new realm —
see `AuthorizationSeeder` in the setup code):

### System Admin
```
permissions: ["realm:admin"]
```
Granted to the first user of the realm (System-Admin group with
`BoundTo: ["*"]`). Realm-wide bypass — sees and can do everything in
every app.

### User Manager
```
permissions: [
  "cocoar-auth:user:read", "cocoar-auth:user:write",
  "cocoar-auth:permission-role:read",
  "cocoar-auth:authorization-group:read", "cocoar-auth:authorization-group:write"
]
```
Can maintain users + groups, view but not change role definitions.

### Viewer
```
permissions: [
  "cocoar-auth:user:read",
  "cocoar-auth:permission-role:read",
  "cocoar-auth:authorization-group:read",
  "cocoar-auth:oauth-client:read", "cocoar-auth:oauth-scope:read"
]
```
Read-only auditor.

Admins can adjust these roles or create more — they aren't
hard-coded.

## Setup bootstrap

On first-time setup of a realm:

1. The `cocoar-auth` system app is registered (`AppRealmSeeder`)
2. Three default roles are created (System Admin, User Manager,
   Viewer) with `AppSlug = "cocoar-auth"`
3. A default group "System Admin" is created with the System-Admin
   role and `BoundTo = ["*"]`
4. The first registered user is placed into the System-Admin group
5. Result: the first user holds `realm:admin` and sees the full
   sidebar in every app registered in this realm

Code in `Cocoar.Auth.Authorization/Setup/` (setup hook) and
`Cocoar.Auth.Authentication.Setup` (user seeding).

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
