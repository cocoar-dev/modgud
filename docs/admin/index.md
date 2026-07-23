# Administration overview

The administration area appears in the sidebar as soon as your account holds **at least one admin read permission** (see [Roles](./roles)). Realm administrators with `realm:admin` see everything; "granular" admins (e.g. a user manager) only see the areas they have rights for.

::: tip First time setting this up?
If you've just installed Modgud and want to bind your first SaaS app, start with the [SaaS App Integration Walkthrough](../integrate/saas-walkthrough) — it's the linear path.
:::

## Areas

### Identity & Access

- [Users](./users) — create, edit, lock, unlock, GDPR-erase accounts
- [Roles](./roles) — permission bundles per app
- [Groups](./groups) — who is a member of what role; static or scripted

### Apps

Modgud is **multi-app capable**: every SaaS application in a realm is registered as its own App with its own resources, roles, and OAuth bindings.

- [Applications](./applications) — register apps and curate their permission catalogs; each App can also override a slice of realm config (branding, self-registration, native grants, DCR, CIMD) on its **Settings** tab — unset fields inherit the realm

### OAuth & OpenID Connect

Modgud is not just a login frontend — it's a full **OAuth 2.0 / OpenID Connect provider** built on OpenIddict. Third-party apps sign in via OIDC instead of maintaining their own user databases.

- [OAuth Clients](./oauth-clients) — apps that sign in through the IdP (web, mobile, CLI)
- [OAuth Scopes](./oauth-scopes) — which capabilities (scopes) are available?
- [OAuth APIs (Resource Servers)](./oauth-apis) — register backends that validate tokens
- [Dynamic Client Registration](./dynamic-client-registration) — let AI agents (Claude Code, Cursor, MCP clients) register themselves as OAuth clients
- [Invite Codes](./invite-codes) — mint and manage single-use codes for invite-gated self-registration

### Federation & Realms

- [Login Providers](./login-providers) — built-in Internal plus external OIDC (Google, Microsoft, Entra, any OIDC); step-by-step setup walkthroughs included
- [Realms](./realms) — multi-tenant setup; each tenant gets its own database
- [Declarative Realm Provisioning](./realm-provisioning) — create/update/tear down a whole realm from one JSON manifest (realm-as-code, per-test realms, agent automation); serves a fetchable schema
- [Realm Settings](./realm-settings) — realm-admin-owned config (self-registration, DCR policy, branding)

### Customization

Per-realm look and feel. SPA-shell branding plus a beta page-builder editor.

- [Branding](../platform/branding) — product name, primary color, logo, favicon
- [Asset Library](../platform/assets) — upload images for branding and page schemas; SVG sanitisation built in
- [Pages (Beta)](../platform/pages) — Realm defaults plus per-Application overrides for login / signed-out / forgot-password; end-to-end gated by a [feature flag](../operate/feature-flags)

### Operations

- [Observability](../operate/observability) — OpenTelemetry metrics + tracing + in-app live activity feed
- [Logs](./auth-log) — realm-owned **Audit** and **Security** tabs; the Control Plane additionally gets a separate PII-free **Platform** tab
- [Change Requests](./change-requests) — approve profile changes (when the approval flow is enabled)
- [Settings](../platform/settings) — 2FA enforcement, grace period, SMTP, …
- [Feature Flags](../operate/feature-flags) — operator-level toggles for beta / WIP surfaces
- [Recovery CLI](../operate/recovery-cli) — when the UI no longer responds

## Permissions: the two-segment model

Modgud manages permissions as **`<resource>:<action>`** strings. The app is never part of the string — it comes from context: a Role belongs to exactly one App, so the same string means different things depending on which App's Role grants it. Examples:

| Permission | Meaning |
| --- | --- |
| `user:read` | Read the user list (via a Role in the `modgud` app) |
| `oauth-client:write` | Manage OAuth clients (via a Role in the `modgud` app) |
| `todo:write` | Write todos (via a Role in the `acme-tasks` app) |
| `realm:admin` | **Realm-wide bypass** — everything in every app |
| `user:admin` | Resource-wide bypass for "user", within the app that granted it |

Two bypass tiers keep permission lists short:

- **`realm:admin`** — realm-wide. Whoever holds it may do anything in any app.
- **`<resource>:admin`** — resource-wide, within the app the grant came from.

::: info Who is a realm admin?
The first admin in every realm — created via the recovery CLI or the Control-Plane-issued bootstrap invite (see [First-time setup](../getting-started/first-time-setup)) — is automatically placed into the `Administrators` group whose `BoundTo: ["*"]` wildcard makes them effective in every app. Add more admins by putting users into that group (or any other group with equivalent rights).
:::

## Granular gating

The sidebar automatically hides everything you can't read. Examples:

- **Realm admin** (`realm:admin`) — sees and may do everything, in every app
- **User manager** in modgud — `user:read` + `:write` + `session:read` + `auth-log:read` → only the user/session area
- **OAuth manager** in modgud — `oauth-client:*` + `oauth-scope:*` + `oauth-api:*` → only the OAuth area
- **Acme-Tasks Editor** (in the `acme-tasks` app) — `todo:write` + `project:write` → not an admin in modgud, but very much in `acme-tasks`

## Typical workflows

### Bind a new SaaS app

Full step-by-step walkthrough: [SaaS App Integration](../integrate/saas-walkthrough) — realm admin → app → OAuth client → resource server → group/role → backend code.

### Onboard a new employee

1. [Create the user](./users) (first name, last name, email)
2. **Send the sign-in link** — the user sets their password and 2FA themselves
3. Add them to the right [groups](./groups) — those already carry the right roles + BoundTo to the right apps
4. Done — the user can log in and has the right permissions in every connected app

### Wire up external SSO (Microsoft Entra)

Full step-by-step walkthrough: [Login Providers](./login-providers).

### Run multiple tenants

Each tenant gets its own [realm](./realms) — own database, own users, own roles. Routing is per subdomain (`tenant1.auth.acme.example`, `tenant2.auth.acme.example`).

### Admin locked out

[Recovery CLI](../operate/recovery-cli) — a shell tool inside the container that bypasses the UI and writes directly to the database.

## Cloning entities

Most admin list views support **right-click → Clone** on a row (Applications, OAuth Clients, OAuth Scopes, OAuth APIs, Roles, Groups) — it opens the Create modal pre-filled from the source, so standing up a near-duplicate (or an effective rename, since some slugs are immutable) doesn't mean retyping everything by hand.

## Real-time updates

Most admin lists (Users, OAuth Clients/Scopes/APIs, Service Accounts, Scheduled Jobs, Invite Codes, …) refresh themselves automatically when another admin (or you in a second tab) changes something — a live push channel, no manual reload needed. A few areas aren't push-driven yet and either poll on an interval or need a manual refresh: Applications, the Logs page, and Change Requests.
