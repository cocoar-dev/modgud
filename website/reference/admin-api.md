# Admin-Endpoints

Endpoints unter `/api/admin/...` (oder `/api/...` für Resource-Reads
ohne `admin/`-Prefix). Realm wird über das Host-Header aufgelöst.

Jeder Endpoint ist gegated über
`.RequiresPermission("<resource>:<action>")`. Strings sind exakt
dieselben wie im Frontend-Sidebar.

## Users

Endpoint-Definitionen in
`Cocoar.Auth.Api/Features/Users/UsersEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/users` | `user:read` |
| `GET` | `/api/users/{id}` | `user:read` |
| `POST` | `/api/users` | `user:write` |
| `PATCH` | `/api/users/{id}` | `user:write` |
| `DELETE` | `/api/users/{id}` | `user:delete` |
| `POST` | `/api/users/{id}/unlock` | `user:write` |

### Admin GDPR

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/gdpr/delete-request` | `user:delete` |
| `POST` | `/api/admin/users/{id}/gdpr/delete-confirm` | `user:delete` |
| `DELETE` | `/api/admin/users/{id}/gdpr/delete-cancel` | `user:delete` |

### Admin Sessions

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/sessions` | `user:read` |
| `DELETE` | `/api/admin/users/{id}/sessions` | `user:write` (Force Logout) |

### Admin Magic-Link

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/magic-link` | `user:write` |

### Admin 2FA-Grace-Period

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/grace` | `user:read` |
| `PATCH` | `/api/admin/users/{id}/grace` | `user:write` |

### Admin Profile-Change-Requests

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/change-requests` | `user:read` |
| `POST` | `/api/admin/change-requests/{id}/approve` | `user:write` |
| `POST` | `/api/admin/change-requests/{id}/reject` | `user:write` |

## Roles

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/roles` | `permission-role:read` |
| `GET` | `/api/roles/{id}` | `permission-role:read` |
| `POST` | `/api/roles` | `permission-role:write` |
| `PATCH` | `/api/roles/{id}` | `permission-role:write` |
| `DELETE` | `/api/roles/{id}` | `permission-role:delete` |

## Groups

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/groups` | `authorization-group:read` |
| `GET` | `/api/groups/{id}` | `authorization-group:read` |
| `POST` | `/api/groups` | `authorization-group:write` |
| `PATCH` | `/api/groups/{id}` | `authorization-group:write` |
| `DELETE` | `/api/groups/{id}` | `authorization-group:delete` |

## Principals (Polymorphic Read-API)

Liefert User + Groups + ServiceAccounts gemixt — für Suche und
Member-Picker im Frontend.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/principals?search=...` | `user:read` (für Persons) und/oder `authorization-group:read` |

## OAuth Clients

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/clients` | `oauth-client:read` |
| `GET` | `/api/admin/oauth/clients/{id}` | `oauth-client:read` |
| `POST` | `/api/admin/oauth/clients` | `oauth-client:write` |
| `PATCH` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` |
| `DELETE` | `/api/admin/oauth/clients/{id}` | `oauth-client:delete` |
| `POST` | `/api/admin/oauth/clients/{id}/rotate-secret` | `oauth-client:write` |

## OAuth Scopes

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/scopes` | `oauth-scope:read` |
| `GET` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:read` |
| `POST` | `/api/admin/oauth/scopes` | `oauth-scope:write` |
| `PATCH` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:write` |
| `DELETE` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:delete` |

## OAuth APIs

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/apis` | `oauth-api:read` |
| `GET` | `/api/admin/oauth/apis/{id}` | `oauth-api:read` |
| `POST` | `/api/admin/oauth/apis` | `oauth-api:write` |
| `PATCH` | `/api/admin/oauth/apis/{id}` | `oauth-api:write` |
| `DELETE` | `/api/admin/oauth/apis/{id}` | `oauth-api:delete` |

## Login-Provider (Internal + External)

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/login-providers` | `login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}` | `login-provider:read` |
| `POST` | `/api/admin/login-providers` | `login-provider:write` |
| `PATCH` | `/api/admin/login-providers/{id}` | `login-provider:write` |
| `DELETE` | `/api/admin/login-providers/{id}` | `login-provider:delete` |

## IdP-Config (OIDC Identity Providers)

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/idp-config` | `idp-config:read` |
| `GET` | `/api/admin/idp-config/{id}` | `idp-config:read` |
| `POST` | `/api/admin/idp-config` | `idp-config:write` |
| `PATCH` | `/api/admin/idp-config/{id}` | `idp-config:write` |
| `DELETE` | `/api/admin/idp-config/{id}` | `idp-config:delete` |
| `POST` | `/api/admin/idp-config/{id}/rotate-secret` | `idp-config:write` |
| `POST` | `/api/admin/idp-config/{id}/test-script` | `idp-config:read` |

## Realms

Nur in Realms mit `CanManageTenants = true` (i.d.R. nur System-Realm).
Sonst 404. Siehe [Realm-API](/reference/realm-api).

## Auth-Log

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?from=...&to=...` | `auth-log:read` |

## App-Settings

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/app-settings` | `app:admin` |
| `PATCH` | `/api/admin/app-settings` | `app:admin` |

## Projection-Endpoints (Maintenance)

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/projections` | `app:admin` |
| `POST` | `/api/admin/projections/{name}/rebuild` | `app:admin` |

## Permission-Checks im Detail

`PermissionEndpointFilter` läuft nach Authentication:

```
1. ClaimTypes.NameIdentifier → UserId
2. IPermissionService.GetEffectivePermissionsAsync(userId)
3. Bypass-Check: app:admin? → ✓
4. Bypass-Check: <resource>:admin? → ✓
5. Exact-Check: needed permission? → ✓
6. sonst → 403
```

Effektive Permissions kommen aus der BFS über alle Group-Memberships
des Users (transitiv, inkl. Nested), expandiert über die assigned
PermissionRoles.

## Pagination

List-Endpoints unterstützen:

| Param | Typ | Bedeutung |
|---|---|---|
| `page` | int | 1-basiert |
| `pageSize` | int | Items pro Seite |
| `search` | string | Volltext-Suche |
| `sortBy` | string | Sort-Field |
| `sortDescending` | bool | Sort-Richtung |

Antwort:

```json
{
  "items": [ ... ],
  "totalCount": 234,
  "page": 1,
  "pageSize": 50
}
```

## Real-Time-Updates

Nach jeder Mutation feuert das Backend ein
SignalR-Event über den `UIHub`. Das Frontend
(`useEntityService`-Composable) hört darauf und refreshed automatisch
die betroffenen Listen — kein manuelles Polling nötig.

Hub-Endpoint: `/signalr/ui` (mit Auth-Cookie + WebSocket-Upgrade).
