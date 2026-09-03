# Admin endpoints

Endpoints under `/api/admin/...` (or `/api/...` for resource reads
without the `admin/` prefix). The realm is resolved via the Host
header.

Update endpoints follow the shared [write semantics](/reference/#write-semantics): merge-patch by default (absent = unchanged, explicit `null` clears, `[]` clears a list), with the handful of full-replace endpoints called out there.

Most endpoints are cookie-only and gated through
`.RequiresPermission("<resource>:<action>")`, and those permission
strings are exactly the same as in the frontend sidebar. A few
endpoints are gated by authentication only (e.g. the principal/lookup
pickers) or are public (the anonymous SPA-bootstrap reads); these are
called out per-row below. Routes marked **Management API** additionally
accept an OAuth bearer token with scope `modgud.management`, audience
`urn:modgud:management-api`, and the same live permission. See the
[Management API guide](/integrate/management-api).

## Users

Endpoint definitions in
`Modgud.Api/Features/Users/UsersEndpoints.cs`. The resource path is
singular (`/api/user`). Deletes are gated by `user:write` — there is no
separate `user:delete` tier. There is no `unlock` endpoint.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/user/lookup` | Authenticated (no specific permission) |
| `GET` | `/api/user` | `user:read` |
| `GET` | `/api/user/{id}` | `user:read` |
| `POST` | `/api/user` | `user:write` |
| `PUT` | `/api/user/{id}` | `user:write` |
| `DELETE` | `/api/user/{id}` | `user:write` |
| `DELETE` | `/api/user` | `user:write` (bulk delete, ids in body) |
| `POST` | `/api/user/{id}/restore` | `user:write` |
| `POST` | `/api/user/restore` | `user:write` (bulk restore, ids in body) |
| `PUT` | `/api/user/{id}/password` | `user:write` |
| `PUT` | `/api/user/{id}/active` | `user:write` |
| `GET` | `/api/user/{id}/groups` | `user:read` (direct + inherited group memberships) |
| `GET` | `/api/user/{id}/effective-groups` | `user:read` (live effective membership, incl. auto-match diagnostics) |
| `POST` | `/api/user/{id}/groups` | `user:write` (add to a group) |
| `DELETE` | `/api/user/{id}/groups/{groupId}` | `user:write` (remove direct membership) |

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
| `GET` | `/api/group/{id}/effective-members` | `authorization-group:read` |
| `POST` | `/api/group` | `authorization-group:write` |
| `PUT` | `/api/group/{id}` | `authorization-group:write` |
| `DELETE` | `/api/group/{id}` | `authorization-group:write` |

## Principals (polymorphic lookup API)

Returns active persons, groups, and service accounts mixed — used by
the member picker in the frontend. The single endpoint is a lookup
(`/api/principal/lookup`) that returns all active, non-deleted
principals; it has no `search` query parameter and is gated by
`authorization-group:read` (a zero-role user cannot dump the
directory).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/principal/lookup` | `authorization-group:read` |

## Service accounts

The non-human leg of the Principal hierarchy — for `client_credentials`
(machine-to-machine) callers. Endpoint definitions in
`Modgud.Api/Features/ServiceAccounts/ServiceAccountsEndpoints.cs`.
Deletes are gated by `service-account:write` — there is no
`service-account:delete` tier; deleting a service account
cascade-deletes its credentials and revokes their outstanding tokens.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/service-account` | `service-account:read` |
| `GET` | `/api/service-account/{id}` | `service-account:read` |
| `POST` | `/api/service-account` | `service-account:write` |
| `PUT` | `/api/service-account/{id}` | `service-account:write` |
| `DELETE` | `/api/service-account/{id}` | `service-account:write` |

## Positions

Position reads are the first general Management API surface. They accept the
normal Modgud admin cookie or a management bearer token; the remaining Position
and terminal routes are cookie-only. Every route returns 404 while the
`PositionTerminals` feature flag is disabled.

| Method | Path | Permission | Authentication |
|---|---|---|---|
| `GET` | `/api/position` | `position:read` | Cookie or Management API bearer |
| `GET` | `/api/position/{id}` | `position:read` | Cookie or Management API bearer |
| `POST` | `/api/position` | `position:write` | Cookie only |
| `PUT` | `/api/position/{id}` | `position:write` | Cookie only |
| `DELETE` | `/api/position/{id}` | `position:write` | Cookie only |

### Service account credentials

A "credential" is a confidential OAuth client pinned to the service
account with the single `client_credentials` grant — the only
mutation path for these SA-managed OAuth clients (`/api/admin/oauth/clients`
rejects mutating them directly).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/service-account/{id}/credentials` | `service-account:read` |
| `POST` | `/api/service-account/{id}/credentials` | `service-account:write` |
| `PUT` | `/api/service-account/{id}/credentials/{credId}` | `service-account:write` |
| `POST` | `/api/service-account/{id}/credentials/{credId}/rotate` | `service-account:write` |
| `DELETE` | `/api/service-account/{id}/credentials/{credId}` | `service-account:write` |

## OAuth clients

Deletes are gated by `oauth-client:write` — there is no
`oauth-client:delete` tier. Client creation is also a **Management API** route.
Terminal intent additionally requires `position:write`; linking or creating a
Service Account additionally requires `service-account:write`.

| Method | Path | Permission | Authentication |
|---|---|---|---|
| `GET` | `/api/admin/oauth/clients` | `oauth-client:read` | Cookie only |
| `GET` | `/api/admin/oauth/clients/{id}` | `oauth-client:read` | Cookie only |
| `POST` | `/api/admin/oauth/clients` | `oauth-client:write` plus conditional resource permission | Cookie or Management API bearer |
| `PUT` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` | Cookie only |
| `DELETE` | `/api/admin/oauth/clients/{id}` | `oauth-client:write` | Cookie only |
| `POST` | `/api/admin/oauth/clients/{id}/regenerate-secret` | `oauth-client:write` | Cookie only |

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

The Security log reads only the current realm's physical database. The
Control-Plane-only Platform log reads PII-free deployment events from the
Global Store. Both accept `category`, `eventType`, and `limit`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?category=...&eventType=...&limit=...` | `auth-log:read` |
| `GET` | `/api/admin/platform-audit?category=...&eventType=...&limit=...` | `control-plane:platform-audit:read` |

Neither surface has a clear/delete endpoint. Retention jobs delete only
expired entries.

## App info

The SPA bootstrap reads public realm/branding metadata anonymously.
Realm-wide admin-owned config lives under `/api/realm-settings`; the
**per-Application** override of that config rides inline on the App
resource (see [Application settings](#application-settings) below).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/app-info` | Anonymous |

`/api/app-info` also publishes the resolved (App ⊕ realm)
`RegistrationFields` policy (`Email` always `Required`; `Username` / `Firstname`
/ `Lastname` one of `Off` / `Optional` / `Required`) so clients render exactly
the identity inputs the realm or App requires. See
[Registration Fields](/admin/realm-settings#registration-fields).

## Applications

The per-realm list of registered apps. Endpoint definitions in
`Modgud.Api/Features/Admin/Apps/AppsEndpoints.cs`. The system app (`modgud`)
is seeded automatically, cannot be created through this API, and cannot be
deleted — only its display name / description / permission catalog can be
edited. Deletes are gated by `app:write` and blocked (409, with a body
listing every blocker) while the App's permission catalog is still
referenced by a role or resource server.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/app/lookup` | Authenticated (no specific permission) |
| `GET` | `/api/app` | `app:read` |
| `GET` | `/api/app/{id}` | `app:read` |
| `DELETE` | `/api/app/{id}` | `app:write` |

`POST /api/app` and `PUT /api/app/{id}` are documented below, alongside the
per-Application settings override that rides on the same calls.

## Application settings

Per-Application override of the realm defaults (ADR-0011). An App is **one
resource**, so this override is not a separate endpoint — it rides **inline**
on the App as a `Settings` field, read and written in the same call as the
rest of the App (one tenant transaction).

- **Read** — `GET /api/app/{id}` returns `Settings` next to the catalog. It is
  **sparse**: only the sections the App overrides are present; a `null`/absent
  section inherits the realm. (The list endpoint `GET /api/app` omits it.)
- **Write** — `POST /api/app` and `PUT /api/app/{id}` carry `Settings` as the
  **complete desired override state** (a replace, not a merge): a present
  section sets that override, a `null` section clears it (→ inherit the realm),
  and within a section a `null` field inherits that field. Setting
  `Origin.Subdomain` also writes the global host→App routing map (clearing it
  removes the route).

See [Applications](/admin/applications#application-settings).

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/app/{id}` | `app:read` |
| `POST` | `/api/app` | `app:write` |
| `PUT` | `/api/app/{id}` | `app:write` |

The body sections are `Origin` (subdomain), `Branding`, `EmailBranding`,
`SelfRegistration` (incl. the `Posture` = `Off` / `JitOnOtp` /
`ExplicitEndpoint`), `RegistrationFields` (per-field `Username` / `Firstname` /
`Lastname` = `Off` / `Optional` / `Required`, each `null` = inherit),
`NativeGrants`, `Dcr`, and `Cimd` — each nullable, each mirroring the
realm-level shape minus the pieces that stay realm-only (captcha, DCR GC
interval).

## Invite codes

Single-use registration invite codes for the `InviteCode` self-registration
posture, scoped to one App. Endpoint definitions in
`Modgud.Api/Features/InviteCodes/InviteCodeEndpoints.cs`. Dual-auth: each
mutating/reading call accepts either the admin cookie (gated on the
`invite-code:read`/`invite-code:write` permission) or an M2M bearer token
carrying the app-bound `invite:read`/`invite:write` OAuth scope. The mint
endpoint returns the plaintext code exactly once; only its hash is stored.

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/app/{appId}/invite-codes` | `invite-code:write` (or scope `invite:write`) |
| `GET` | `/api/app/{appId}/invite-codes` | `invite-code:read` (or scope `invite:read`) |
| `DELETE` | `/api/app/{appId}/invite-codes/{id}` | `invite-code:write` (or scope `invite:write`) |
| `GET` | `/api/admin/invite-codes` | `invite-code:read` (realm-wide overview across every App; admin-only) |

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
