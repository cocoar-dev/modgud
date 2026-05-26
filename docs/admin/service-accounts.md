---
title: Service Accounts
description: Machine identities that authenticate via OAuth client_credentials, sit in the same Group/Role/Permission model as humans, and produce clean audit trails.
---

# Service Accounts

A **Service Account** (SA) is a non-human principal — a build agent, an integration, a scheduled job. It carries a stable account name, lives in the same `Principal → Group → Role → Permission` model as a `Person`, but has no email, no password, no MFA. Machines authenticate by exchanging an OAuth `client_secret` for an access token; Modgud then resolves the token's `sub` claim to the owning Service Account so audit logs read `ci.build-agent did X` instead of `client_id=4f7a9b…e3 did X`.

Create a Service Account whenever a non-interactive caller — CI runner, scheduled sync, server-to-server integration — needs to act against a Modgud-protected API.

## Surface

- Admin grid: **`/admin/service-accounts`**
- Permissions: `service-account:read` (view), `service-account:write` (create, edit, delete, issue credentials, rotate, delete credentials)
- Backing API group: `/api/service-account` (and `/api/service-account/{id}/credentials` for the credential children)

## Two layers: ServiceAccount vs OAuth Client

A working M2M setup needs two objects living in two layers. The split is deliberate.

| Layer | Object | Answers |
| --- | --- | --- |
| Authorization / identity | **ServiceAccount** | *Who* is acting — stable identity for audit logs, group membership, role and permission grants. |
| Credential / wire | **OAuth Client** (`client_credentials`) | *How* it authenticates — `client_id`, `client_secret`, scopes, lifetimes, rotation. |

### Why both

Modgud has a unified permission model that has to work identically for humans and machines:

- `bwi` (Person) → group `data-engineers` → role `data-read` → permission `acme-tasks:data:read`
- `ci.build-agent` (ServiceAccount) → group `data-engineers` → role `data-read` → permission `acme-tasks:data:read`

If the OAuth client carried the machine identity directly, the entire group/role/permission graph would have to be duplicated on the client side, and audit logs would surface opaque `client_id` strings. The industry pattern is consistent: Keycloak auto-creates a hidden "service account user" behind every `client_credentials` client, AWS IAM separates Roles from access keys, GCP IAM separates Service Accounts from JSON keys. Modgud surfaces both layers explicitly because admins need to manage them — but the SA is the user-facing concept and the OAuth client is an implementation detail of "how does this SA authenticate".

## Strict grant separation

A single OAuth client serves **exactly one** identity model:

| Client kind | Allowed grants | Linked SA? | Token `sub` |
| --- | --- | --- | --- |
| User-facing | `authorization_code` (+ `refresh_token`, `device_code`, …) | must be null | `Person.Id` of the logged-in user |
| Service-account credential | `client_credentials` only | required | `ServiceAccount.Id` |

::: warning No mixing
A client with both `authorization_code` and `client_credentials` enabled would make `sub` ambiguous (logged-in user on the user flow, …what? on the M2M flow). The OAuth admin endpoints reject this at validation time — see `OAuthAdminService.cs` for the invariant guards.
:::

Concretely:

- An SA-managed client (`LinkedServiceAccountId != null`) is **read-only via `/admin/oauth/clients`**. Mutations route through the SA-scoped credential endpoints so the link can't be silently dropped. Attempting `PUT /api/oauth/client/{id}` on an SA-managed client returns `CannotMutateServiceAccountManagedClient`.
- Adding `client_credentials` to a client that has no linked SA is rejected — no ownerless M2M clients.
- The `authorization_code` / `device_code` / `implicit` paths refuse to issue a token whose client has a `LinkedServiceAccountId`.

## Creating a Service Account

1. Open `/admin/service-accounts` and click **Create**.
2. Fill in:
   - **Account name** — lowercase letters, digits, dots, hyphens or underscores; 2-64 chars; starts with a letter or digit. This is the audit-log handle (`ci.build-agent`, `integrations.acme-tasks`, `nightly.sync`). Unique across the whole principal table — a Person and a ServiceAccount can't share an account name, because both can act as the login handle in different contexts.
   - **Purpose** (optional) — free text. Pure documentation; the authorization layer never reads it.
3. **Create**. The SA exists but has no credentials yet — services can't authenticate as it.

The new account appears in the grid. Open it to manage credentials, group membership, and the active toggle (deactivating an SA causes its `/connect/token` requests to be refused immediately, even with otherwise-valid credentials).

## Issuing credentials (the OAuth client behind the SA)

Credentials are managed exclusively from the **SA detail modal → Credentials** section. The global `/admin/oauth-clients` grid shows the resulting clients with an **M2M** column listing the linked SA name, but double-clicking an SA-managed row navigates straight back to the SA modal — there's only one place to edit them.

To issue a credential:

1. Open the SA, scroll to **Credentials**, click **Issue credential**.
2. Pick the scopes the caller needs and (optionally) the Apps the credential is bound to. Grant types are system-pinned to `["client_credentials"]` and not user-editable.
3. **Save**. The server creates a confidential OAuth client with:
   - `client_id` auto-generated as `{AccountName}.{8-char-suffix}` (e.g. `ci.build-agent.k7f2x9n3`)
   - `client_secret` hashed in the database, shown **once** in a copy-to-clipboard panel with a "won't be shown again" warning
   - `LinkedServiceAccountId` pointing at this SA
4. Copy the secret into the caller's secret store (GitHub Action secret, Kubernetes secret, vault entry, …). Closing or refreshing the modal loses it permanently — at that point only **Rotate secret** can mint a new one.

### Rotate and delete

- **Rotate secret** generates a fresh `client_secret`, invalidates the old one immediately, and surfaces the new one in the same one-time-display panel. Use it for periodic rotation or after suspected exposure.
- **Delete** removes the OAuth client. Tokens issued before the delete remain valid until their natural expiry (Modgud does not currently revoke outstanding tokens at delete time); no new tokens can be minted.

### 1:N

One SA can own multiple credentials. Useful for:

- **Zero-downtime rotation** — issue a second credential, switch the caller, delete the old one.
- **Per-caller scope narrowing** — `ci.build-agent.read` with `builds:read` only, `ci.build-agent.write` with `builds:write`; both log as `ci.build-agent`.
- **Multiple environments under one identity** — dev-CI, staging-CI, prod-CI share the audit name `ci.build-agent` but hold independent secrets.

`N:1` (multiple SAs sharing one OAuth client) is forbidden by the same `sub`-ambiguity argument that bans mixed-grant clients.

## How a caller gets a token

Plain OAuth `client_credentials` — no SA-specific wire protocol:

```bash
curl -X POST https://idp.example.com/connect/token \
  -d "grant_type=client_credentials" \
  -d "client_id=ci.build-agent.k7f2x9n3" \
  -d "client_secret=<secret>" \
  -d "scope=builds:write" \
  -d "resource=https://acme-tasks.example.com"
```

The token endpoint loads the OAuth client, follows `LinkedServiceAccountId` to the SA, verifies the SA isn't deleted or inactive, and issues an access token whose `sub` is the SA's `Id`.

## Token contents

For an SA-issued token:

- `sub` — `ServiceAccount.Id`
- `name` — `ServiceAccount.AccountName`
- `scope` — exactly what was requested (and allowed by the linked client)
- `resource_access` — per-audience `roles` and `permissions` blocks built from the SA's group/role/permission chain, embedded directly in the access token. The `client_credentials` flow has no UserInfo round-trip in practice, so the resource server gets everything it needs from the JWT itself. The shape mirrors what human tokens carry via UserInfo per audience.

The downstream API validates the token, reads `sub`, and gates access exactly the same way it does for a Person — the permission evaluator doesn't care whether the principal is a Person or a ServiceAccount.

## Group and role membership

A Service Account can be added to any group from `/admin/groups` the same way a Person can. JsEval auto-membership scripts can target SAs too: they receive `principal.type == "service-account"` and can branch on `accountName`, `purpose`, or any group/permission predicate.

Concretely, granting permissions to a Service Account is the same three-step path as for a human: put it in a group, give the group a role, give the role the permissions it needs. There is no special "service-account-only role" — humans and machines pull from the same role catalogue.

## Audit log

Every token issue, group membership change, credential rotation, and delete attributes to the SA's `AccountName`, not to the raw `client_id`. The auth log shows entries like `ci.build-agent triggered build` rather than `client_id=4f7a9b…e3 triggered build`. Downstream apps that log against `sub`/`name` claims get the same readable handle.

## Cascade delete

Deleting a Service Account (`DELETE /api/service-account/{id}`) cascade-deletes every credential owned by the SA in one transaction, then soft-deletes the SA itself. The response includes `DeletedCredentialCount` so the UI can confirm the blast radius. Soft-delete (rather than hard-delete) keeps audit-log references resolvable — historical entries still hydrate the SA name.

There is no "unlink credential" operation. The only way to detach a credential from its SA is delete-and-reissue under a different SA.

## Migrating pre-2C clients

Realms that existed before the Service-Account-credentials feature shipped may still hold standalone `client_credentials` clients with no `LinkedServiceAccountId`. The token endpoint falls back to the legacy `sub = client_id` behaviour for these so production callers keep working, but their tokens skip the SA-derived `resource_access` block and they show up in audit as raw client IDs.

To migrate them in one shot, run the recovery CLI:

```bash
dotnet Modgud.Api.dll recover migrate-cc-credentials [--realm <slug>]
```

For each un-linked `client_credentials` client the command auto-provisions a Service Account named `legacy.{clientId}`, links the client to it, and leaves a re-runnable trail (already-linked clients are skipped; existing `legacy.*` SAs are re-used). Defaults to the `system` realm; pass `--realm` to scope to a specific tenant. After migration, rename the SA from the admin UI or merge it into a properly-named one.

## Related

- [OAuth Clients](./oauth-clients) — the global grid that lists user-facing clients alongside SA-managed credentials with an M2M column linking back here.
- [Groups](./groups) — where SAs pick up roles and permissions.
- [Applications](./applications) and [OAuth Scopes](./oauth-scopes) — the resources and scopes a credential's tokens can target.
- [Auth Log](./auth-log) — filter for the SA's account name to see every action it has taken.
