# Glossary

This page defines the terminology used in Cocoar.Auth and how it maps to other identity systems.

## Core Concepts

### Realm

An isolated identity boundary. Each realm has its own users, roles, OAuth clients, and database. This is the same concept as a **realm** in Keycloak or a **tenant** in Auth0/Azure AD.

The **system realm** is the first realm, created automatically. It can manage other realms.

### User

A person or service account within a realm. Users belong to exactly one realm. Each user has credentials (password, 2FA), a profile, and assigned roles.

### Role

A named set of permissions within a realm. Roles are realm-scoped — an "Admin" role in realm A is independent from "Admin" in realm B.

The built-in `Admin` role grants access to the admin UI and all admin API endpoints within that realm.

### Session

A server-side record of an active login. Tracks IP, browser, device, and last activity. Users can view and revoke their own sessions. Admins can force-logout users.

---

## OAuth / OIDC Terminology

These terms come from the OAuth 2.0 and OpenID Connect standards. If you've used Keycloak, Auth0, or similar systems, most of these will be familiar.

### Client

An application registered to authenticate users or access APIs within a realm.

Examples: a web app, a mobile app, a backend service.

Each client has:
- **Client ID** — public identifier (e.g., `my-app`)
- **Client Secret** — private key (only for confidential clients like backend services)
- **Redirect URIs** — allowed callback URLs after login
- **Grant Types** — which authentication flows are allowed

### Scope

A permission boundary that a client can request. Scopes control what information and access a token grants. Users see the requested scopes on the consent screen before granting access.

**Built-in scopes:**
- `openid` — Required for OIDC, returns user ID
- `profile` — First name, last name
- `email` — Email address
- `roles` — User's role memberships
- `offline_access` — Allows long-lived sessions (refresh tokens)

**Custom scopes** can be created per realm for application-specific permissions (e.g., `billing:read`).

### API

A protected backend API that clients can request access to. If you've used Keycloak, this is similar to configuring a resource server. In Auth0, it's called an "API".

An API:
- Has a unique name (e.g., `billing-api`) that identifies it in tokens
- Has its own secret for token validation
- Is associated with one or more scopes

### Grant Type

The authentication flow used by a client to obtain tokens.

| Grant Type | Use Case |
|-----------|----------|
| **Authorization Code** (+ PKCE) | Web apps, SPAs, mobile apps — anything where a user logs in |
| **Client Credentials** | Machine-to-machine, background services — no user involved |
| **Refresh Token** | Renew expired access tokens without re-login |

::: warning No Implicit or ROPC
Cocoar.Auth does **not** support the Implicit flow or Resource Owner Password Credentials (ROPC). These are considered insecure and deprecated by the OAuth 2.1 specification.
:::

### Token Types

| Type | What it is |
|------|-----------|
| **Access Token** | Grants access to APIs. Can be a **reference token** (opaque, validated via introspection) or a **JWT** (self-contained, validated locally). Configured per client — see below. |
| **Identity Token** | Contains user information (name, email, roles). Used by the client app to know who logged in. |
| **Refresh Token** | Allows obtaining new access tokens without asking the user to log in again. |

### Access Token Types

Each client can be configured to use one of two access token formats:

| Format | How it works | Best for |
|--------|-------------|----------|
| **Reference Token** (default) | Opaque string. APIs validate by calling Cocoar.Auth's introspection endpoint. | SPAs, mobile apps, public clients — instant revocation, no secrets exposed. |
| **JWT** | Self-contained signed token. APIs validate locally using the signing key. | Trusted backend services — no introspection roundtrip needed. |

::: tip When to use which?
**Reference tokens** are the safer default. When you revoke a reference token, it stops working immediately. JWTs can't be revoked — they remain valid until they expire. Use JWTs only for trusted services where the performance benefit of skipping introspection matters.
:::

---

## Comparison with Other Systems

| Concept | Cocoar.Auth | Keycloak | Auth0 | Azure AD |
|---------|-------------|----------|-------|----------|
| Isolation boundary | Realm | Realm | Tenant | Tenant (Directory) |
| Application | Client | Client | Application | App Registration |
| Permission set | Role | Role | Role | Role |
| Protected APIs | API | Resource scope | API | App Role |
| Token format | Reference or JWT (per client) | JWT (default) | JWT (default) | JWT (default) |
| Discovery | Per-realm | Per-realm | Per-tenant | Per-tenant |

---

## Login Provider

An external identity source (Google, Microsoft, SAML IdP) that users can authenticate with. Each realm can configure its own set of login providers independently.

The built-in `Internal` provider represents local username/password authentication.
