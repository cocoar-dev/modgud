# Admin endpoints

Endpoints under `/api/admin/...` (or `/api/...` for resource reads
without the `admin/` prefix). The realm is resolved via the Host
header.

Every endpoint is gated through
`.RequiresPermission("<resource>:<action>")`. The strings are exactly
the same as in the frontend sidebar.

## Users

Endpoint definitions in
`Modgud.Api/Features/Users/UsersEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/users` | `modgud:user:read` |
| `GET` | `/api/users/{id}` | `modgud:user:read` |
| `POST` | `/api/users` | `modgud:user:write` |
| `PATCH` | `/api/users/{id}` | `modgud:user:write` |
| `DELETE` | `/api/users/{id}` | `modgud:user:delete` |
| `POST` | `/api/users/{id}/unlock` | `modgud:user:write` |

### Admin GDPR

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/gdpr/delete-request` | `modgud:user:delete` |
| `POST` | `/api/admin/users/{id}/gdpr/delete-confirm` | `modgud:user:delete` |
| `DELETE` | `/api/admin/users/{id}/gdpr/delete-cancel` | `modgud:user:delete` |

### Admin sessions

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/sessions` | `modgud:user:read` |
| `DELETE` | `/api/admin/users/{id}/sessions` | `modgud:user:write` (force logout) |

### Admin magic link

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/magic-link` | `modgud:user:write` |

### Admin 2FA grace period

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/grace` | `modgud:user:read` |
| `PATCH` | `/api/admin/users/{id}/grace` | `modgud:user:write` |

### Admin profile change requests

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/change-requests` | `modgud:user:read` |
| `POST` | `/api/admin/change-requests/{id}/approve` | `modgud:user:write` |
| `POST` | `/api/admin/change-requests/{id}/reject` | `modgud:user:write` |

## Roles

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/roles` | `modgud:permission-role:read` |
| `GET` | `/api/roles/{id}` | `modgud:permission-role:read` |
| `POST` | `/api/roles` | `modgud:permission-role:write` |
| `PATCH` | `/api/roles/{id}` | `modgud:permission-role:write` |
| `DELETE` | `/api/roles/{id}` | `modgud:permission-role:delete` |

## Groups

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/groups` | `modgud:authorization-group:read` |
| `GET` | `/api/groups/{id}` | `modgud:authorization-group:read` |
| `POST` | `/api/groups` | `modgud:authorization-group:write` |
| `PATCH` | `/api/groups/{id}` | `modgud:authorization-group:write` |
| `DELETE` | `/api/groups/{id}` | `modgud:authorization-group:delete` |

## Principals (polymorphic read API)

Returns users, groups, and service accounts mixed — used by search and
the member picker in the frontend.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/principals?search=...` | `modgud:user:read` (for persons) and/or `modgud:authorization-group:read` |

## OAuth clients

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/clients` | `modgud:oauth-client:read` |
| `GET` | `/api/admin/oauth/clients/{id}` | `modgud:oauth-client:read` |
| `POST` | `/api/admin/oauth/clients` | `modgud:oauth-client:write` |
| `PATCH` | `/api/admin/oauth/clients/{id}` | `modgud:oauth-client:write` |
| `DELETE` | `/api/admin/oauth/clients/{id}` | `modgud:oauth-client:delete` |
| `POST` | `/api/admin/oauth/clients/{id}/rotate-secret` | `modgud:oauth-client:write` |

## OAuth scopes

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/scopes` | `modgud:oauth-scope:read` |
| `GET` | `/api/admin/oauth/scopes/{id}` | `modgud:oauth-scope:read` |
| `POST` | `/api/admin/oauth/scopes` | `modgud:oauth-scope:write` |
| `PATCH` | `/api/admin/oauth/scopes/{id}` | `modgud:oauth-scope:write` |
| `DELETE` | `/api/admin/oauth/scopes/{id}` | `modgud:oauth-scope:delete` |

## OAuth APIs

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/apis` | `modgud:oauth-api:read` |
| `GET` | `/api/admin/oauth/apis/{id}` | `modgud:oauth-api:read` |
| `POST` | `/api/admin/oauth/apis` | `modgud:oauth-api:write` |
| `PATCH` | `/api/admin/oauth/apis/{id}` | `modgud:oauth-api:write` |
| `DELETE` | `/api/admin/oauth/apis/{id}` | `modgud:oauth-api:delete` |

## Login providers

The single endpoint group for both built-in (Internal) and external (Oidc /
Saml / Ldap / Kerberos) login providers. The Internal entry is auto-seeded
once per realm and rejects edits / deletes — clients identify it by
`IsBuiltIn=true` on the DTO.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/login-providers` | `modgud:login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}` | `modgud:login-provider:read` |
| `GET` | `/api/admin/login-providers/flavors` | `modgud:login-provider:read` |
| `POST` | `/api/admin/login-providers` | `modgud:login-provider:write` |
| `PUT` | `/api/admin/login-providers/{id}` | `modgud:login-provider:write` |
| `DELETE` | `/api/admin/login-providers/{id}` | `modgud:login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/enable` | `modgud:login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/disable` | `modgud:login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/secret` | `modgud:login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/test-user-update` | `modgud:login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}/last-raw-claims` | `modgud:login-provider:read` |

## Realms

Only available on the **Control-Plane realm** (the realm flagged
`IsControlPlane = true`). Otherwise 404 — the existence of realm CRUD is
hidden from tenant realms. Permissions live under the `control-plane`
app slug (`control-plane:realm:read|write`), not under `modgud`. See
[Realm API](/reference/realm-api) for the request/response shapes and the
`InitialAdmin` requirement on `POST /api/admin/realms`.

## Auth log

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?from=...&to=...` | `modgud:auth-log:read` |

## App settings

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/app-settings` | `realm:admin` |
| `PATCH` | `/api/admin/app-settings` | `realm:admin` |

## Projection endpoints (maintenance)

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/projections` | `realm:admin` |
| `POST` | `/api/admin/projections/{name}/rebuild` | `realm:admin` |

## Permission checks in detail

`PermissionEndpointFilter` runs after authentication. Permission
strings are fully qualified as `<app>:<resource>:<action>`; the
filter splits the requested permission and resolves it against the
user's effective permissions for that app:

```
1. ClaimTypes.NameIdentifier → UserId
2. needed permission → split into (appSlug, resource, action)
3. IPermissionService.GetUserPermissionsAsync(UserId, appSlug)
   ├── BFS over the user's groups (transitive, with visited set)
   ├── filter to groups whose BoundTo contains appSlug or "*"
   └── filter their roles to AppSlug == appSlug
4. Bypass check: realm:admin? → ✓
5. Exact check: needed permission? → ✓
6. Bypass check: <app>:admin? → ✓
7. Bypass check: <app>:<resource>:admin? → ✓
8. otherwise → 403
```

Effective permissions are computed per request from the BFS over all
the user's group memberships (transitive, including nested),
expanded through the assigned PermissionRoles.

## Pagination

List endpoints support:

| Param | Type | Meaning |
|---|---|---|
| `page` | int | 1-based |
| `pageSize` | int | Items per page |
| `search` | string | Full-text search |
| `sortBy` | string | Sort field |
| `sortDescending` | bool | Sort direction |

Response:

```json
{
  "items": [ ... ],
  "totalCount": 234,
  "page": 1,
  "pageSize": 50
}
```

## Real-time updates

After every mutation the backend fires a SignalR event over the
`UIHub`. The frontend (`useEntityService` composable) listens and
automatically refreshes the affected lists — no manual polling needed.

Hub endpoint: `/signalr/ui` (with auth cookie + WebSocket upgrade).
