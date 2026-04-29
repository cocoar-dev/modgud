# Permissions & Gating

cocoar.auth nutzt **granulares Per-Resource-Gating**: jeder Endpoint
und jeder Sidebar-Eintrag prüft einen einzelnen Permission-String, der
exakt zwischen Backend und Frontend gespiegelt ist.

## Permission-Format

`<resource>:<action>` — z.B.:

| Permission | Bedeutung |
|---|---|
| `user:read` | Liste/Detail von Usern lesen |
| `user:write` | User erstellen/bearbeiten |
| `user:delete` | User löschen (Soft + GDPR) |
| `user:admin` | Per-Resource-Bypass für alle User-Actions |
| `oauth-client:read` | OAuth-Clients lesen |
| `oauth-client:write` | OAuth-Clients erstellen/bearbeiten |
| `oauth-client:delete` | OAuth-Clients löschen |
| `permission-role:read` | Rollen lesen |
| `authorization-group:write` | Gruppen erstellen/bearbeiten |
| `realm:write` | Realms erstellen/bearbeiten |
| `app:admin` | **Globaler Bypass** (alle Resources, alle Actions) |

## Resources in cocoar.auth

| Resource | Wofür |
|---|---|
| `user` | User-Verwaltung (Cocoar.Auth.Authentication.ApplicationUser) |
| `permission-role` | Rollen-Verwaltung |
| `authorization-group` | Gruppen-Verwaltung |
| `oauth-client` | OAuth-Client-Verwaltung |
| `oauth-scope` | OAuth-Scope-Verwaltung |
| `oauth-api` | OAuth-API-Resource-Verwaltung |
| `login-provider` | Internal-/External-Login-Provider |
| `idp-config` | OIDC-IdP-Konfigurationen |
| `realm` | Realm-CRUD (nur in Realms mit `CanManageTenants = true`) |
| `auth-log` | AuthLog lesen |
| `app` | nur als globaler `app:admin`-Bypass relevant |

Registriert beim Boot:

```csharp
// AddInfrastructure → AddCocoarAuthAuthorization(opts => { ... })
opts.RegisterResource("user");
opts.RegisterResource("permission-role");
opts.RegisterResource("authorization-group");
// ...
```

## Backend-Gating: `RequiresPermission`

Endpoints gaten via `EndpointFilter`-Extension:

```csharp
app.MapGet("/api/admin/users", async (...) => { ... })
   .RequiresPermission("user:read");

app.MapPost("/api/admin/users", async (...) => { ... })
   .RequiresPermission("user:write");

app.MapDelete("/api/admin/users/{id}", async (...) => { ... })
   .RequiresPermission("user:delete");
```

Der Filter (`PermissionEndpointFilter`):

1. Liest `ClaimTypes.NameIdentifier` aus `HttpContext.User`
2. Lädt den User + alle Gruppen via `IPermissionService.GetEffectivePermissionsAsync`
3. Prüft:
   - `app:admin` vorhanden? → durch
   - `<resource>:admin` für die geforderte Resource? → durch
   - Exakt diese Permission? → durch
   - Sonst → `403 Forbidden`

## Frontend-Gating: Sidebar + Buttons

Der `auth.store.ts` (Pinia) lädt die effektiven Permissions des
aktuellen Users beim Login mit:

```typescript
// permissions: string[]  z.B. ["user:read", "user:write", "oauth-client:read"]

function hasPermission(needed: string): boolean {
  if (this.permissions.includes('app:admin')) return true
  const [resource] = needed.split(':')
  if (this.permissions.includes(`${resource}:admin`)) return true
  return this.permissions.includes(needed)
}
```

Sidebar-Items in `views/admin/AdminView.vue` deklarieren, welche
Permissions sie sichtbar machen:

```typescript
const allNavItems: NavItem[] = [
  { section: 'authorization', label: 'nav.users',  icon: 'users',
    path: '/admin/users',  requirePermissions: ['user:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window',
    path: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
  // ...
]

function canSee(item: NavItem): boolean {
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}
```

Sektionen werden ausgeblendet wenn alle ihre Items gefiltert sind. Ein
User mit nur `user:read` sieht nur die Authorization-Sektion mit
"Users" — keine OAuth, keine System.

## Per-Realm-Domain-Erweiterung

Wenn ein Realm `CanManageTenants = true` hat (in cocoar.auth nur der
System-Realm), bekommt seine User zusätzlich Zugriff auf den
`realm`-Resource. Die User aller anderen Realms sehen die Realm-Liste
gar nicht — `realm:read` ist in deren `permission-role`s nicht
verfügbar.

Das wird im Frontend durch Sichtbarkeit gehandhabt; im Backend ist es
strikter: `RealmsEndpoints` prüfen sowohl `realm:read`/`realm:write`
**als auch** dass der aktuelle Realm `CanManageTenants = true` ist.
Sonst 404 (nicht 403, weil die Existenz von Realm-CRUD nicht geleakt
werden soll).

## Default-Roles

Der First-Time-Setup erstellt drei Default-Roles (einmal pro neuem
Realm — siehe `AuthorizationSeeder` im Setup-Code):

### System Admin
```
permissions: ["app:admin"]
```
Bekommt der erste User des Realms (System-Admin-Group). Globaler
Bypass — sieht und kann alles.

### User Manager
```
permissions: [
  "user:read", "user:write",
  "permission-role:read",
  "authorization-group:read", "authorization-group:write"
]
```
Kann User + Gruppen pflegen, Rollen-Definitionen anschauen aber nicht
ändern.

### Viewer
```
permissions: [
  "user:read",
  "permission-role:read",
  "authorization-group:read",
  "oauth-client:read", "oauth-scope:read"
]
```
Read-only-Auditor.

Admin kann diese Rollen anpassen oder weitere erstellen — sie sind
nicht eingebrannt.

## Setup-Bootstrap

Beim First-Time-Setup eines Realms:

1. Drei Default-Rollen werden angelegt (System Admin, User Manager,
   Viewer)
2. Eine Default-Gruppe "System Admin" wird angelegt mit der
   System-Admin-Rolle
3. Der erste registrierte User wird in die System-Admin-Gruppe
   aufgenommen
4. Ergebnis: der erste User hat `app:admin` und sieht die volle Sidebar

Code in `Cocoar.Auth.Authorization/Setup/` (Setup-Hook) und
`Cocoar.Auth.Authentication.Setup` (User-Seeding).

## Permission-Auflösung im Detail

```
Request mit JWT/Cookie kommt rein
  ↓
PermissionEndpointFilter
  ↓
ClaimTypes.NameIdentifier → UserId
  ↓
IPermissionService.GetEffectivePermissionsAsync(userId)
  ├── BFS durch alle Group-Membership (transitiv, mit Visited-Set)
  ├── für jede Gruppe: load PermissionRole-Refs
  ├── für jede Rolle: expand Permissions
  └── Set<string> aller "<resource>:<action>"
  ↓
Checks:
  hat "app:admin"? → ✓
  hat "<resource>:admin"? → ✓
  hat exakt needed? → ✓
  sonst → 403
```

Die Auflösung ist scoped pro Request, nicht gecached. Das ist
absichtlich: Permissions ändern sich live (Admin kickt User aus
Gruppe), und cocoar.auth ist nicht
performance-kritisch (Admin-UI-Traffic, nicht Hot-Path).

Wenn das mal anders wird: ein `IMemoryCache` mit Sliding-Expiration
(z.B. 30 Sekunden) und Cache-Invalidation auf
`GroupMembershipRecomputedEvent` würde reichen.
