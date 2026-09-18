---
title: Management API
description: Let a backend or delegated administrator call selected Modgud administration endpoints through OAuth without duplicating Modgud's permission model.
---

# Management API

Modgud exposes selected administration operations to trusted consumers through
the same API routes used by its own admin UI. This is a general Modgud contract:
consumers do not receive a separate, application-specific administration API.

Three caller types share the same endpoint permission:

| Caller | Authentication | Typical use |
|---|---|---|
| Modgud admin UI | Modgud application cookie | Interactive first-party administration |
| Delegated person | Authorization Code + PKCE access token | A consumer performs an admin action on behalf of its signed-in operator |
| Service account | `client_credentials` access token | Synchronization, provisioning, or other unattended administration |

The OAuth layer only establishes that the client may target the Management API.
The caller's current Modgud roles and permissions decide what it may actually do.
There is no second set of fine-grained OAuth scopes to keep in sync.

## Fixed OAuth contract

Every realm automatically contains this protected scope:

| Field | Value |
|---|---|
| Scope | `modgud.management` |
| Resource / audience | `urn:modgud:management-api` |

A bearer token must contain both values. The client must also be registered in
the same realm, and the token subject must resolve to an active Person or Service
Account. Dynamic Client Registration cannot opt a client into this protected
scope.

## Machine-to-machine setup

Use this flow when no person is present:

1. Create a role containing only the required Modgud permissions. For the
   Position reads, grant `position:read`. Terminal provisioning needs both
   `position:write` and `oauth-client:write`. Reading an Application's complete
   Principal scope needs `app-scope:read`.
2. Create or choose a group, attach the role, and add the Service Account as a
   member.
3. On that Service Account, issue a credential and allow the
   `modgud.management` scope. Modgud pins the resulting OAuth client to
   `client_credentials` and to that Service Account. Assign the OAuth client to
   each Application whose scope it may read; scope reads never treat an empty
   `AppIds` list as permission to read every Application.
4. Store the one-time client secret in the consumer's secret store.

Request a token with the standard OAuth wire format:

```bash
curl -X POST https://idp.example.com/connect/token \
  -d "grant_type=client_credentials" \
  -d "client_id=<client-id>" \
  -d "client_secret=<client-secret>" \
  -d "scope=modgud.management" \
  -d "resource=urn:modgud:management-api"
```

Then call an exposed endpoint:

```bash
curl https://idp.example.com/api/position \
  -H "Authorization: Bearer <access-token>"
```

The token's `sub` is the Service Account id. A token from an unlinked or
differently linked client is rejected even if its claims were otherwise
well-formed.

## Provision a terminal in one call

`POST /api/admin/oauth/clients` is the generic client-provisioning operation.
When the request carries the staffing grant, Modgud atomically creates the
terminal-managed OAuth client and terminal slot, and either links an existing
Position or creates one inline. Nothing is committed if any part fails.

The consumer MUST choose a stable `clientId`; Modgud does not generate one on
this path. For an existing Position:

```http
POST /api/admin/oauth/clients
Authorization: Bearer <management-access-token>
Content-Type: application/json

{
  "clientId": "alerthub-gate-3",
  "displayName": "AlertHub terminal: Gate 3",
  "clientType": "public",
  "allowedGrantTypes": [
    "urn:cocoar:params:oauth:grant-type:staffing"
  ],
  "linkedPositionPrincipalId": "<position-guid-or-short-guid>",
  "terminalDisplayName": "Gate terminal left",
  "terminalLocation": "Gate 3",
  "terminalBinding": "dpop",
  "webAuthnRpId": "terminal.example.com",
  "scopes": ["alerthub-terminal"],
  "appIds": ["<app-guid-or-short-guid>"]
}
```

To create the Position in the same transaction, omit
`linkedPositionPrincipalId` and send `newPosition` instead:

```json
{
  "accountName": "gate-3",
  "purpose": "Gatehouse response position",
  "terminalPolicy": {
    "enabled": true
  }
}
```

The first successful request returns `201 Created` with `client`,
`createdTerminalId`, and—only for inline creation—`createdPosition`.
`client-secret` binding also returns `clientSecret` once. The returned terminal
ShortGuid is accepted directly by the terminal routes; consumers do not need to
convert it to a canonical GUID.

The caller-selected `clientId` is the retry key:

- the same normalized request returns `200 OK`, the same terminal id, and
  `wasAlreadyProvisioned: true`;
- a different request under the same `clientId` returns `409 Conflict`;
- a replay never repeats a one-time `clientSecret`. If its original response
  was lost, rotate that secret deliberately. DPoP provisioning has no secret to
  recover.

Terminal provisioning evaluates both `oauth-client:write` and
`position:write`. Generic client creation needs `oauth-client:write`; linking
or inline-creating a Service Account additionally needs
`service-account:write`. This prevents a client administrator from minting a
credential for a more privileged machine identity.

## Read an Application's Principal scope

`GET /api/app/{appId}/scope` returns one consistent full read of the Principals
assigned to an Application. It requires `app-scope:read` and accepts the App id
as a Guid or ShortGuid. A bearer caller may only target Applications listed in
its OAuth client's `AppIds`; an empty assignment grants no scope access. A
cookie-authenticated administrator with the same permission may read any
Application in the realm.

There is no separate scope configuration. Modgud derives the result from the
existing group graph:

1. Every active group whose `BoundTo` contains the App slug or `*` is a scope
   root.
2. Each root, its nested groups, and every transitive active member belong to
   the scope.
3. All Principal kinds use the same rule: Person, Position, Service Account,
   and Group.

A group may deliberately have no roles. It then grants no permission while
still assigning its members to the selected Application scopes. The admin UI
marks such a group as **No permissions**.

The response includes an opaque `ScopeVersion`, the contributing root groups,
and the typed Principal records:

```json
{
  "AppId": "<short-guid>",
  "AppSlug": "alert-hub",
  "ScopeVersion": "v1-<opaque-hash>",
  "RootGroups": [
    {
      "Id": "<short-guid>",
      "Name": "AlertHub principals",
      "HasPermissions": false
    }
  ],
  "Principals": [
    {
      "Id": "<short-guid>",
      "Type": "person",
      "DisplayName": "AP | Alice Person",
      "IsActive": true,
      "IsScopeRoot": false,
      "AccountName": "alice",
      "Firstname": "Alice",
      "Lastname": "Person",
      "Acronym": "AP",
      "Email": "alice@example.com"
    }
  ]
}
```

`ScopeVersion` versions the **definition**, not every member. Adding or removing
an App binding, changing the nested-group structure, or changing an automatic
membership predicate changes it and tells a consumer to perform a new full
read. Ordinary direct membership/profile changes leave it stable; the resumable
change stream can therefore represent those as individual changes. Consumers
must treat the version as opaque and compare it for equality only.

## List and revoke an Application's authorizations

An application that shows its users their "connected systems" (an MCP host, a Home Assistant instance, a third-party app) usually stores its own per-connection state, keyed on the access token's `sub` and `oi_au_id`. Blocking a connection on the application's side stops the access token there, but the refresh token lives in Modgud. The connected system is the OAuth client, so the application cannot use RFC 7009 revocation on its behalf. These two operations close that gap.

`GET /api/app/{appId}/authorizations` requires `oauth-authorization:read` and returns the valid authorizations that reach the Application. An authorization reaches an Application when one of its scopes is app-scoped to that Application, or lists one of the Application's [OAuth APIs](/admin/oauth-apis) as a resource. Authorizations that only concern other Applications are never returned.

| Query parameter | Meaning |
|---|---|
| `subject` | Optional. Only this user's authorizations (the token's `sub`). |
| `limit` | Page size, default `200`, maximum `1000`. |
| `after` | The `NextAfter` value of the previous page. |

```json
{
  "Items": [
    {
      "Id": "0b6f1c0e-5d0a-4d0e-9a57-2f2f0f6f4c11",
      "Subject": "5f0c8a52-7f5e-4c7b-8a39-0a3b2f1d9e77",
      "ClientId": "acme-homeassistant",
      "Type": "permanent",
      "Scopes": ["lists.read", "offline_access", "openid"],
      "CreatedAt": "2026-09-18T09:12:44Z"
    }
  ],
  "NextAfter": "0b6f1c0e-5d0a-4d0e-9a57-2f2f0f6f4c11"
}
```

`Id` and `Subject` are returned exactly as they appear in the access token (`oi_au_id`, `sub`), not as ShortGuids, because the consumer joins on them. `ClientId` is absent for a [CIMD](/admin/client-id-metadata-documents) client, which Modgud never persists. `NextAfter` is absent on the last page; keep requesting while it is present. `Type` is `permanent` for a consented Authorization Code grant and `ad-hoc` for a Device Flow or native grant.

`DELETE /api/app/{appId}/authorizations/{id}` requires `oauth-authorization:revoke`. It revokes the authorization and every token issued under it, the refresh token included, ends a native client session built on it, and writes a `security.authorization_revoked` audit event. It answers `204 No Content`, also when the authorization was revoked already. An unknown id and an authorization outside the Application's reach both answer `404`.

An already-issued JWT access token stays cryptographically valid until it expires. The application blocks it on its side by the same `oi_au_id`; reference tokens and the next refresh fail at Modgud immediately. The next authorize flow of that client shows the consent screen and creates a new authorization with a new `oi_au_id`.

As with the scope read, a bearer caller may only target Applications listed in its OAuth client's `AppIds`.

## Delegated-person setup

Use this flow when the consumer should act with the permissions of a signed-in
administrator:

1. Give the Person the required Modgud permission through the normal
   Group → Role → Permission chain.
2. Register a user-flow OAuth client using Authorization Code + PKCE. Do not add
   `client_credentials` to this client.
3. Allow `openid` and `modgud.management` on the client.
4. Include both `scope=openid modgud.management` and
   `resource=urn:modgud:management-api` in the authorization request. Repeat the
   resource indicator at the token exchange as required by RFC 8707.

The API resolves the Person from `sub` and evaluates the same live permission
as for the Modgud admin cookie. A user-flow token issued to a Service-Account-
linked client is rejected.

## Live authorization and revocation

Fine-grained permissions are evaluated when the request reaches Modgud, not
copied into a management-specific scope or trusted from an old token. Therefore:

- removing a role or group assignment affects subsequent calls without waiting
  for a new access token;
- removing `modgud.management` from the OAuth client or disabling/deleting the
  client blocks already-issued management tokens at the next API call;
- `realm:admin` and resource-level `*:admin` follow the normal Modgud permission
  evaluator;
- deactivating or deleting the Person or Service Account prevents further use;
- credential rotation/deletion and Service Account deactivation also run the
  existing OAuth revocation cascade.

An invalid or inactive identity returns `401`. A valid identity with the wrong
audience, missing management scope, or insufficient permission returns `403`
with a stable `Management.*` problem code.

## Currently exposed operations

Management bearer access is opt-in per endpoint. Adding the authentication
scheme does not turn every cookie-only admin route into a remote API.

| Method | Path | Live permission | Additional condition |
|---|---|---|---|
| `GET` | `/api/position` | `position:read` | `PositionTerminals` enabled |
| `GET` | `/api/position/{id}` | `position:read` | `PositionTerminals` enabled |
| `GET` | `/api/app/{id}/scope` | `app-scope:read` | Full `BoundTo`-derived Principal snapshot |
| `GET` | `/api/app/{id}/authorizations` | `oauth-authorization:read` | Valid authorizations that reach the Application; paged |
| `DELETE` | `/api/app/{id}/authorizations/{authorizationId}` | `oauth-authorization:revoke` | Revokes the authorization and all its tokens; idempotent |
| `GET` | `/api/app/{id}/change-feed/snapshot` | `app-scope:read` | Feed enabled; full public synchronization snapshot |
| `GET` | `/api/app/{id}/change-feed` | `app-scope:read` | Feed enabled; resumable HTTP read using an opaque cursor |
| `GET` | `/api/app/{id}/change-feed/stream` | `app-scope:read` | Feed enabled; bearer-only resumable SSE stream using the same cursor and envelope |
| `POST` | `/api/admin/oauth/clients` | `oauth-client:write` | `position:write` for terminal provisioning; `service-account:write` for an SA link |
| `GET` | `/api/admin/realm-config/manifest-schema` | `realm:admin` | Current request host selects the realm |
| `GET` | `/api/admin/realm-config/export` | `realm:admin` | Exports only the host-selected realm |
| `POST` | `/api/admin/realm-config/apply` | `realm:admin` | Applies only to the host-selected realm; a foreign manifest slug is rejected |

Direct Position creation, mutation, deletion, grants, terminal enrollment, and
all other admin resources remain cookie-only until their contracts are
deliberately added and tested. The atomic OAuth-client create above is the
supported remote terminal-provisioning path. The
[Admin endpoint reference](/reference/admin-api) is the source of truth for the
exposed surface, and every write body follows the shared
[write semantics](/reference/#write-semantics) (merge-patch: absent = unchanged,
explicit `null` clears, `[]` clears a list — this includes `apply` manifests).

## Security rules for consumers

- Use a dedicated OAuth client for each authentication mode. Modgud forbids
  mixing `client_credentials` with user-facing grants.
- Request only the management scope on credentials intended to administer
  Modgud; ordinary application API scopes are a separate concern.
- Keep Service Account credentials in a backend or secret store. Do not put a
  client secret in browser code.
- Treat Position display names as presentation data. Persist the stable Position
  id as the foreign key and denormalize the current display/account name only as
  a snapshot if useful.

## Related

- [Service Accounts](/admin/service-accounts) — machine identity, credentials,
  rotation, and group membership
- [Permissions & gating](/concepts/permissions) — the live authorization model
- [Application change feed](./application-change-feed) — full sync, cursor,
  retention, SSE, and HTTP fallback
- [OAuth / OpenIddict](./oauth) — token flows and RFC 8707 resource indicators
- [Positions & Terminals](/admin/positions) — administering the first exposed
  resource
