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

### Admin sessions

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/sessions` | `user:read` |
| `DELETE` | `/api/admin/users/{id}/sessions` | `user:write` (force logout) |

### Admin magic link

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/magic-link` | `user:write` |

### Admin 2FA grace period

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/grace` | `user:read` |
| `PATCH` | `/api/admin/users/{id}/grace` | `user:write` |

### Admin profile change requests

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

## Principals (polymorphic read API)

Returns users, groups, and service accounts mixed — used by search and
the member picker in the frontend.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/principals?search=...` | `user:read` (for persons) and/or `authorization-group:read` |

## OAuth clients

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/clients` | `oauth-client:read` |
| `GET` | `/api/admin/oauth/clients/{id}` | `oauth-client:read` |
| `POST` | `/api/admin/oauth/clients` | `oauth-client:write` |
| `PATCH` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` |
| `DELETE` | `/api/admin/oauth/clients/{id}` | `oauth-client:delete` |
| `POST` | `/api/admin/oauth/clients/{id}/rotate-secret` | `oauth-client:write` |

## OAuth scopes

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

## Login providers

The single endpoint group for both built-in (Internal) and external (Oidc /
Saml / Ldap / Kerberos) login providers. The Internal entry is auto-seeded
once per realm and rejects edits / deletes — clients identify it by
`IsBuiltIn=true` on the DTO.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/login-providers` | `login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}` | `login-provider:read` |
| `GET` | `/api/admin/login-providers/flavors` | `login-provider:read` |
| `POST` | `/api/admin/login-providers` | `login-provider:write` |
| `PUT` | `/api/admin/login-providers/{id}` | `login-provider:write` |
| `DELETE` | `/api/admin/login-providers/{id}` | `login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/enable` | `login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/disable` | `login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/secret` | `login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/test-user-update` | `login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}/last-raw-claims` | `login-provider:read` |

## Realms

Only available on the **Control-Plane realm** (the realm flagged
`IsControlPlane = true`). Otherwise 404 — the existence of realm CRUD is
hidden from tenant realms. Permissions live under the `control-plane`
app slug (`realm:read|write`), not under `modgud`. See
[Realm API](/reference/realm-api) for the request/response shapes and the
`InitialAdmin` requirement on `POST /api/admin/realms`.

## Auth log

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?from=...&to=...` | `auth-log:read` |

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
strings are **two-segment** `<resource>:<action>` inside one App's
catalog — the app context is implicit from the calling endpoint group
(`modgud` for the realm-internal admin surface, `control-plane` for
the realm-CRUD endpoints):

```
1. ClaimTypes.NameIdentifier → UserId
2. The calling endpoint determines the app context (modgud or
   control-plane).
3. IPermissionService.GetUserPermissionsAsync(UserId, appSlug)
   ├── BFS over the user's groups (transitive, with visited set)
   ├── filter to groups whose BoundTo contains appSlug or "*"
   ├── filter their roles to AppId == app.Id (or IsRealmAdmin = true)
   └── bypass-pre-expand realm:admin and <r>:admin
4. PermissionEvaluator.Evaluate(grants, "<resource>:<action>"):
     has realm:admin?            → ✓
     has the exact permission?   → ✓
     has <resource>:admin?       → ✓
     otherwise                   → 403
```

Effective permissions are computed per request from the BFS over all
the user's group memberships (transitive, including nested), expanded
through the assigned PermissionRoles. See
[Permissions & gating](../concepts/permissions) for the
full evaluator + UserInfo-emission story.

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
