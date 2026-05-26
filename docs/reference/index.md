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
  authorize, token, userinfo, introspection, discovery, JWKS,
  end-session, device-code, dynamic registration.

## Authentication

- [Auth Endpoints](./auth-api) — `/api/account/*` and friends: login,
  register, magic-link, 2FA enrolment, password reset, change-request
  submission, bootstrap-invite consumption.

## Administration

- [Admin Endpoints](./admin-api) — the tenant-admin surface under
  `/api/admin/*`: users, groups, roles, OAuth client/scope/API CRUD,
  login providers, IdP config, auth log, change-request review.
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
