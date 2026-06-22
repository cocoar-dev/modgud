# Admin endpoints

Endpoints under `/api/admin/...` (or `/api/...` for resource reads
without the `admin/` prefix). The realm is resolved via the Host
header.

Most endpoints are gated through
`.RequiresPermission("<resource>:<action>")`, and those permission
strings are exactly the same as in the frontend sidebar. A few
endpoints are gated by authentication only (e.g. the principal/lookup
pickers) or are public (the anonymous SPA-bootstrap reads); these are
called out per-row below.

## Users

Endpoint definitions in
`Modgud.Api/Features/Users/UsersEndpoints.cs`. The resource path is
singular (`/api/user`). Deletes are gated by `user:write` — there is no
separate `user:delete` tier. There is no `unlock` endpoint.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/user` | `user:read` |
| `GET` | `/api/user/{id}` | `user:read` |
| `POST` | `/api/user` | `user:write` |
| `PUT` | `/api/user/{id}` | `user:write` |
| `DELETE` | `/api/user/{id}` | `user:write` |
| `DELETE` | `/api/user` | `user:write` (bulk delete, ids in body) |
| `POST` | `/api/user/{id}/restore` | `user:write` |
| `PUT` | `/api/user/{id}/password` | `user:write` |
| `PUT` | `/api/user/{id}/active` | `user:write` |

### Admin GDPR

Permanent (irreversible) PII erasure. Soft-delete goes through the
regular user CRUD path above; this is the masking flow, gated on the
dedicated `gdpr:admin` permission and requires a `Reason` in the body.

| Method | Path | Permission |
|---|---|---|
| `DELETE` | `/api/admin/users/{id}/permanent` | `gdpr:admin` |

### Admin sessions

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/sessions` | `session:read` |
| `DELETE` | `/api/admin/users/{id}/sessions` | `session:write` (force logout) |

### Admin magic link

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/users/{id}/magic-link` | `user:write` |

### Admin 2FA grace period

All gated on `user:write` (the whole group shares the gate).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/security-info` | `user:write` |
| `PUT` | `/api/admin/users/{id}/grace/policy` | `user:write` |
| `POST` | `/api/admin/users/{id}/grace/reset` | `user:write` |
| `DELETE` | `/api/admin/users/{id}/grace` | `user:write` |

### Admin external links

Definitions in `Modgud.Authentication/Api/ExternalAuth/ProfileLinkEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/users/{id}/external-links` | `user:read` |
| `DELETE` | `/api/admin/users/{id}/external-links/{linkId}` | `user:write` (force-unlink) |

Force-unlink shares the self-service unlink semantics: it hard-removes the link
(freeing the `(issuer, subject)` slot for re-linking) and is refused if it would
strip the user's only remaining authentication method.

### Admin profile change requests

All gated on `user:write` (the whole group shares the gate).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/change-requests` | `user:write` |
| `POST` | `/api/admin/change-requests/{id}/approve` | `user:write` |
| `POST` | `/api/admin/change-requests/{id}/reject` | `user:write` |

## Roles

Singular path (`/api/role`). Deletes are gated by `permission-role:write` —
there is no `permission-role:delete` tier.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/role` | `permission-role:read` |
| `GET` | `/api/role/{id}` | `permission-role:read` |
| `POST` | `/api/role` | `permission-role:write` |
| `PUT` | `/api/role/{id}` | `permission-role:write` |
| `DELETE` | `/api/role/{id}` | `permission-role:write` |

## Groups

Singular path (`/api/group`). Deletes are gated by `authorization-group:write` —
there is no `authorization-group:delete` tier.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/group` | `authorization-group:read` |
| `GET` | `/api/group/{id}` | `authorization-group:read` |
| `POST` | `/api/group` | `authorization-group:write` |
| `PUT` | `/api/group/{id}` | `authorization-group:write` |
| `DELETE` | `/api/group/{id}` | `authorization-group:write` |

## Principals (polymorphic lookup API)

Returns active persons, groups, and service accounts mixed — used by
the member picker in the frontend. The single endpoint is a lookup
(`/api/principal/lookup`) that returns all active, non-deleted
principals; it has no `search` query parameter and is gated by
authentication only (any signed-in user), not a specific permission.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/principal/lookup` | Authenticated (no specific permission) |

## OAuth clients

Deletes are gated by `oauth-client:write` — there is no
`oauth-client:delete` tier.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/clients` | `oauth-client:read` |
| `GET` | `/api/admin/oauth/clients/{id}` | `oauth-client:read` |
| `POST` | `/api/admin/oauth/clients` | `oauth-client:write` |
| `PUT` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` |
| `DELETE` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` |
| `POST` | `/api/admin/oauth/clients/{id}/regenerate-secret` | `oauth-client:write` |

## OAuth scopes

Deletes are gated by `oauth-scope:write` — there is no
`oauth-scope:delete` tier.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/scopes` | `oauth-scope:read` |
| `GET` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:read` |
| `POST` | `/api/admin/oauth/scopes` | `oauth-scope:write` |
| `PUT` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:write` |
| `DELETE` | `/api/admin/oauth/scopes/{id}` | `oauth-scope:write` |

## OAuth APIs

Deletes are gated by `oauth-api:write` — there is no `oauth-api:delete`
tier. The `create-implicit-scope` convenience endpoint is gated on
`oauth-scope:write` (the side-effect being authorised is the scope
creation, not the API edit).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/oauth/apis` | `oauth-api:read` |
| `GET` | `/api/admin/oauth/apis/{id}` | `oauth-api:read` |
| `POST` | `/api/admin/oauth/apis` | `oauth-api:write` |
| `PUT` | `/api/admin/oauth/apis/{id}` | `oauth-api:write` |
| `DELETE` | `/api/admin/oauth/apis/{id}` | `oauth-api:write` |
| `POST` | `/api/admin/oauth/apis/{id}/create-implicit-scope` | `oauth-scope:write` |

## Login providers

The single endpoint group for both built-in (Internal) and external (Oidc /
Saml / Ldap / Kerberos) login providers. The Internal entry is auto-seeded
once per realm and rejects edits / deletes — clients identify it by
`IsBuiltIn=true` on the DTO.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/login-providers/flavors` | `login-provider:read` |
| `GET` | `/api/admin/login-providers` | `login-provider:read` |
| `GET` | `/api/admin/login-providers/{id}` | `login-provider:read` |
| `POST` | `/api/admin/login-providers` | `login-provider:write` |
| `PUT` | `/api/admin/login-providers/{id}` | `login-provider:write` |
| `DELETE` | `/api/admin/login-providers/{id}` | `login-provider:write` |
| `POST` | `/api/admin/login-providers/{id}/secret` | `login-provider:write` |

## Realms

Only available on the **Control-Plane realm** (the realm flagged
`IsControlPlane = true`). Otherwise 404 — the existence of realm CRUD is
hidden from tenant realms. Permissions live under the `control-plane`
app slug (`realm:read|write`), not under `modgud`. See
[Realm API](/reference/realm-api) for the request/response shapes and the
`InitialAdmin` requirement on `POST /api/admin/realms`.

## Security log

The security/audit log. Reads are filtered by `category`, `eventType`,
and `limit` query parameters. Clearing the log is destructive and gated
behind the `realm:admin` bypass.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?category=...&eventType=...&limit=...` | `auth-log:read` |
| `DELETE` | `/api/admin/auth-log` | `realm:admin` |

## App info

The SPA bootstrap reads public realm/branding metadata anonymously.
Realm-wide admin-owned config lives under `/api/realm-settings`; the
**per-Application** override of that config lives under
`/api/app/{id}/settings` (below).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/app-info` | Anonymous |

## Application settings

Per-Application override of the realm defaults (ADR-0011). The
`ApplicationSettings` document is keyed by the App's id and tenant-scoped.
**Sparse** in both directions: `GET` returns only what the App overrides
(a `null` section = inherits the realm); `PATCH` replaces the provided
sections (a `null`/omitted section = no change, and within a provided
section a `null` field = inherit the realm value). Setting
`Origin.Subdomain` also writes the global host→App routing map (and clearing
it removes the route). See [Applications](/admin/applications#application-settings).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/app/{id}/settings` | `app:read` |
| `PATCH` | `/api/app/{id}/settings` | `app:write` |

The body sections are `Origin` (subdomain), `Branding`, `EmailBranding`,
`SelfRegistration` (incl. the `Posture` = `Off` / `JitOnOtp` /
`ExplicitEndpoint`), `NativeGrants`, `Dcr`, and `Cimd` — each nullable,
each mirroring the realm-level shape minus the pieces that stay realm-only
(captcha, DCR GC interval).

## Projection endpoints (maintenance)

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/admin/projections/rebuild` | `realm:admin` |
| `GET` | `/api/admin/projections/consistency-check` | `realm:admin` |

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
