---
title: API Reference
description: HTTP endpoint reference for the OAuth/OIDC surface, the authentication API, the admin API, and the realm-management API.
---

# API Reference

Exhaustive endpoint reference, grouped by purpose. For the *why*
of each surface, see [Concepts](/concepts/) — the reference pages
are deliberately terse and contract-focused.

## OAuth / OIDC

- [OAuth Endpoints](./oauth-api) — `/connect/*` and `/.well-known/*`:
  authorize, token (incl. the native cookieless grants), userinfo,
  introspection, discovery, JWKS, end-session, device-code, dynamic
  registration, consent, bearer passkey management.

## Authentication

- [Auth Endpoints](./auth-api) — `/api/account/*` and friends: login,
  register, magic-link, native passwordless sign-in, 2FA enrolment,
  password reset, change-request submission, bootstrap-invite
  consumption.

## Administration

- [Admin Endpoints](./admin-api) — the tenant-admin surface under
  `/api/admin/*`: users, groups, roles, applications, service
  accounts, invite codes, OAuth client/scope/API CRUD, login
  providers, IdP config, auth log, change-request review.
- [Realm Endpoints](./realm-api) — Control-Plane-only:
  `/api/admin/realms/*`. Realm CRUD with `InitialAdmin` bootstrap
  invite issuance.

## Conventions

All endpoints return JSON. Paginated lists wrap their items in
`{ "Items": [...], "TotalCount": N }`. Errors follow RFC 7807
problem-detail format. Authentication is either a session cookie
(`/api/admin/*`, `/api/account/*` after login) or a Bearer token
(`/api/v1/distribution/*`, resource-server-shaped endpoints) —
each page calls out which.

## Write semantics

Every write endpoint follows exactly one of two models — there is no per-field special-casing to memorize.

### Merge-patch (the default)

Updates are merge-patches in the spirit of [RFC 7386](https://www.rfc-editor.org/rfc/rfc7386). One sentence covers the whole contract:

> A field **absent** from the JSON is left unchanged; a **present** field is applied — an explicit `null` **clears** the value, `[]` clears a list, and booleans have no clear (absent and `null` both mean "unchanged").

In detail:

| You send | Effect |
|---|---|
| field omitted | unchanged |
| `"field": null` | cleared (back to its default / unset) |
| `"field": ""` | also cleared — blank strings are normalized to a clear |
| `"field": value` | set / replaced |
| `"list": []` | list cleared |
| `"list": [..]` | full list replaced |
| `"flag": null` | unchanged — booleans are only ever set with `true`/`false` |

This applies to the OAuth client/scope/API updates, realm settings, realm metadata, login providers, users, positions, service accounts and their credentials, terminal slots, scheduled-job overrides — and identically to [declarative provisioning manifests](/admin/realm-provisioning#apply-merge-vs-prune) and [configuration drafts](/admin/configuration-drafts), which share the same wire shape.

Two field categories never clear: identity/natural-key fields (client id, account name, email, an API's audience) and enum-like fields (consent type, membership mode, access-token type) — those always hold a value, so `null` simply means "unchanged". Immutable fields (a client's type, a provider's flavor) are called out on their endpoints.

### Full replace (declared per endpoint)

A few writes replace a whole document instead of patching fields — the payload *is* the complete desired state, so a `null` (or omitted) optional field is stored as the new value, not skipped:

- **Group update** (`PUT /api/group/{id}`) — the body is the group's full desired state. The one merge-patch field is `BoundTo` (absent = unchanged, `[]` = dormant).
- **Role update** (`PUT /api/role/{id}`) — full payload replace, including the permission set.
- **App update** (`PUT /api/app/{id}`) — replaces identity + permission catalog; the optional `Settings` override document rides along only when present.
- **Per-app settings overrides** (ADR-0011) — override documents where `null` means *inherit from the realm*, replaced as a whole.
- **Inbox retention settings** — a singleton document, stored as sent.

If an endpoint's page doesn't say "full replace", it's a merge-patch.
