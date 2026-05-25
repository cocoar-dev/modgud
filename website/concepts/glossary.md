# Glossary

Terms in modgud and their counterparts in other identity systems.

## Core terms

### Realm

An isolated identity boundary. Each realm has **its own PostgreSQL
database** (`<master-db>_<slug>`), its own users, roles,
OAuth clients, and login providers.

Mapping to other systems:

| modgud | Keycloak | Auth0 | Azure AD |
|---|---|---|---|
| Realm | Realm | Tenant | Tenant (Directory) |

The **system realm** is the first realm, created automatically on first
boot. It starts as the **Control-Plane** realm — flagged
`IsControlPlane = true` — meaning only its users may create further
realms. Exactly one realm per deployment is the Control Plane.

The realm boundary is the **domain** (Host header), not the URL path.
Realm `acme` lives under `acme.example.com`, the system realm under
`system.example.com` or `localhost`.

### User

A human or service account inside a realm. Users belong to exactly
one realm. Identical usernames in different realms are different
accounts.

In code: `Modgud.Authentication.Domain.ApplicationUser` (ASP.NET
Core Identity user).

### Group

An organisational unit. Groups have members (users or other groups)
and carry `PermissionRole` references. Groups exist in two modes:

- **Manual** — admin maintains the member list
- **Auto** — a membership script determines members dynamically

See [Auto membership](/authorization-slice/auto-membership).

### PermissionRole

A named bundle of permissions. Binds to one App (or to the realm when
`IsRealmAdmin = true`) and references a list of the App catalog's
`PermissionIds`:

```
Name:          "User Manager"
AppId:         <modgud app id>
PermissionIds: [user:read, user:write]
```

### Permission

A two-segment string `<resource>:<action>` inside an App's catalog —
the App context is implicit from the catalog container. Examples:
`user:read` (in the `modgud` app), `invoice:write` (in a `billing`
app), `realm:admin` (realm-constant bypass). Permissions flow
exclusively through groups:

```
User → Group → Role → Permission
```

Two bypass tiers: `<resource>:admin` (resource-wide within the calling
app) and `realm:admin` (realm-wide emergency exit). See
[Permissions & gating](/authorization-slice/permissions) for the full
evaluator + UserInfo-emission story.

### Session

A server-side record (`UserSession` Marten document) of an active
login. Tracks IP, browser, OS, device type, `LastActiveAt`,
`ExpiresAt`. Users can revoke their own sessions; admins can force-logout
users. UAParser parses the user agent.

---

## OAuth / OIDC terms

### Client (OAuth application)

An external application that requests user logins or API access.
Created per realm — the same `client_id` in realm A and realm B are
different clients.

Configurable per client:

- **Client ID** — public identifier (e.g. `my-app`)
- **Client Secret** — private key (for confidential clients)
- **Redirect URIs** — allowed callback URLs
- **Grant Types** — which flows are allowed
- **Access Token Type** — Reference (default) or JWT

### Scope

A permission boundary that a client can request. Scopes appear in the
token; resource servers decide based on the scopes whether the request
is OK.

Default scopes (set per realm at realm provisioning):

- `openid` — required for OIDC, returns the user ID
- `profile` — first name, last name
- `email` — email address
- `roles` — role memberships
- `offline_access` — enables refresh tokens

### API (resource)

A protected backend API. Has an identifier (`audience` claim in the
token) and a list of scopes it supports. In code:
`OAuthApiAggregate`.

### Grant Type

| Grant Type | Use case |
|---|---|
| **Authorization Code + PKCE** | Web apps, SPAs, mobile apps |
| **Client Credentials** | Machine-to-machine, background services |
| **Refresh Token** | Renew expired access tokens |

::: warning No Implicit, no ROPC
modgud supports neither Implicit Flow nor Resource Owner
Password Credentials (ROPC). Both are considered insecure and are
deprecated in OAuth 2.1.
:::

### Token types

| Type | What it is |
|---|---|
| **Access Token** | Access to APIs. Reference (opaque, via introspection) or JWT (self-contained) |
| **Identity Token** | Who signed in — consumed by the client |
| **Refresh Token** | Get a new access token without a fresh login |

### Access token format

Configurable per client:

| Format | How it works | Best for |
|---|---|---|
| **Reference** (default) | Opaque string. APIs validate via the introspection endpoint. | SPAs, mobile, public clients — instant revocation. |
| **JWT** | Self-contained, signed token. APIs verify locally. | Trusted backend services — no introspection roundtrip. |

::: tip Which one when?
**Reference tokens** are the safe default. Revoke a reference token
and it is dead immediately. JWTs cannot be revoked — they are valid
until they expire. Use JWT only for trusted services where the
introspection roundtrip gets in the way.
:::

---

## Login providers

An authentication method that users can use. A single `LoginProvider`
aggregate per entry, with a `Type` discriminator. Configurable per realm.

| Type | Status | Description |
|---|---|---|
| **Internal** | Wired up | Built-in username/password. Auto-seeded once per realm, marked `IsBuiltIn=true`, not editable from the admin UI. |
| **Oidc** | Wired up | External OIDC IdPs (Entra ID, Google, Auth0, ...). Authority + client ID + secret + UserUpdateScript. |
| **Saml** / **Ldap** / **Kerberos** | Reserved | Enum values exist; create endpoint rejects with `LoginProvider.TypeNotSupported`. The shape ships now so the FE doesn't have to add a "not supported yet" UI per type later. |

Configured Oidc providers automatically show "Login with {Provider}"
buttons in the login UI. Internal never produces an SSO button — it
backs the local username/password form.

---

## Terms in code

| Term in code | Term in docs/UI | Where |
|---|---|---|
| `TenantId` | Realm slug | Marten/Wolverine, infrastructure layer |
| `Principal` | User or group or service account | Authorization slice (polymorphic) |
| `Person` | User read model in the authorization slice | A subclass of Principal |
| `Aggregate` | Event-sourced entity | Domain layer |
| `*State` | Inline projection for sync consistency | Infrastructure layer |
| `*ListReadModel` / `*DetailsReadModel` | Async projection for read optimization | Infrastructure layer |
| `LoginProvider` | Login provider configuration (Internal / Oidc / ...) | Authentication slice |

::: info "Realm" vs. "Tenant"
User-facing it is **Realm** everywhere. The code uses **Tenant** in
the infrastructure layer (`TenantId`, `ITenantSessionFactory`,
`MasterTableTenancy`), because that is what Marten and Wolverine call
it. Same thing, two names.
:::
