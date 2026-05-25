# Administration overview

The administration area appears in the sidebar as soon as your account holds **at least one admin read permission** (see [Roles](./roles)). Realm administrators with `realm:admin` see everything; "granular" admins (e.g. a user manager) only see the areas they have rights for.

::: tip First time setting this up?
If you've just installed Modgud and want to bind your first SaaS app, start with the [SaaS App Integration Walkthrough](./saas-integration-walkthrough) — it's the linear path.
:::

## Areas

### Identity & Access

- [Users](./users) — create, edit, lock, unlock, GDPR-erase accounts
- [Roles](./roles) — permission bundles per app
- [Groups](./groups) — who is a member of what role; static or scripted

### Apps

Modgud is **multi-app capable**: every SaaS application in a realm is registered as its own App with its own resources, roles, and OAuth bindings.

- [Applications](./applications) — register apps, manage resources, provision a default resource server

### OAuth & OpenID Connect

Modgud is not just a login frontend — it's a full **OAuth 2.0 / OpenID Connect provider** built on OpenIddict. Third-party apps sign in via OIDC instead of maintaining their own user databases.

- [OAuth Clients](./oauth-clients) — apps that sign in through the IdP (web, mobile, CLI)
- [OAuth Scopes](./oauth-scopes) — which capabilities (scopes) are available?
- [OAuth APIs (Resource Servers)](./oauth-apis) — register backends that validate tokens
- [Dynamic Client Registration](./dynamic-client-registration) — let AI agents (Claude Code, Cursor, MCP clients) register themselves as OAuth clients

### Federation & Realms

- [Login Providers](./login-providers) — built-in Internal plus external OIDC (Google, Microsoft, Entra, any OIDC); step-by-step setup walkthroughs included
- [Realms](./realms) — multi-tenant setup; each tenant gets its own database
- [Realm Settings](./realm-settings) — realm-admin-owned config (self-registration, DCR policy, branding)

### Customization

Per-realm look and feel. SPA-shell branding plus a beta page-builder editor.

- [Branding](../plattform/branding) — product name, primary color, logo, favicon
- [Asset Library](../plattform/assets) — upload images for branding (and, later, page schemas); SVG sanitisation built in
- [Pages (Beta)](../plattform/pages) — drag-and-drop editor for login / logout / forgot-password; gated behind a [feature flag](./feature-flags) while the runtime renderer is still being built

### Operations

- [Observability](../plattform/observability) — OpenTelemetry metrics + tracing + in-app live activity feed
- [Auth Log](./auth-log) — audit trail of all login events
- [Change Requests](./change-requests) — approve profile changes (when the approval flow is enabled)
- [Settings](../plattform/settings) — 2FA enforcement, grace period, SMTP, …
- [Feature Flags](./feature-flags) — operator-level toggles for beta / WIP surfaces
- [Recovery CLI](./recovery-cli) — when the UI no longer responds

## Permissions: the three-segment model

Modgud manages permissions in the form **`app:resource:action`**. Examples:

| Permission | Meaning |
| --- | --- |
| `modgud:user:read` | Read the user list in modgud |
| `modgud:oauth-client:write` | Manage OAuth clients in modgud |
| `timetodo:todo:write` | Write todos in the TimeToDo app |
| `realm:admin` | **Realm-wide bypass** — everything in any app |
| `modgud:admin` | App-wide bypass for modgud |
| `modgud:user:admin` | Resource-wide bypass for "user" in modgud |

Three bypass tiers keep permission lists short:

- **`realm:admin`** — realm-wide. Whoever holds it may do anything in any app.
- **`<app>:admin`** — app-wide.
- **`<app>:<resource>:admin`** — resource-wide.

::: info Who is a realm admin?
The first admin in every realm — created via the recovery CLI or the Control-Plane-issued bootstrap invite (see [First-time setup](../getting-started/first-time-setup)) — is automatically placed into the `Administratoren` group whose `BoundTo: ["*"]` wildcard makes them effective in every app. Add more admins by putting users into that group (or any other group with equivalent rights).
:::

## Granular gating

The sidebar automatically hides everything you can't read. Examples:

- **Realm admin** (`realm:admin`) — sees and may do everything, in every app
- **User manager** in modgud — `modgud:user:read` + `:write` + `modgud:session:read` + `modgud:auth-log:read` → only the user/session area
- **OAuth manager** — `modgud:oauth-client:*` + `modgud:oauth-scope:*` + `modgud:oauth-api:*` → only the OAuth area
- **TimeToDo editor** (in the TimeToDo app) — `timetodo:todo:write` + `timetodo:project:write` → not an admin in modgud, but very much in TimeToDo

## Typical workflows

### Bind a new SaaS app

Full step-by-step walkthrough: [SaaS App Integration](./saas-integration-walkthrough) — realm admin → app → OAuth client → default resource server → group/role → backend code.

### Onboard a new employee

1. [Create the user](./users) (first name, last name, email)
2. **Send the sign-in link** — the user sets their password and 2FA themselves
3. Add them to the right [groups](./groups) — those already carry the right roles + BoundTo to the right apps
4. Done — the user can log in and has the right permissions in every connected app

### Wire up external SSO (Microsoft Entra)

Full step-by-step walkthrough: [Login Providers](./login-providers).

### Run multiple tenants

Each tenant gets its own [realm](./realms) — own database, own users, own roles. Routing is per subdomain (`tenant1.auth.firma.at`, `tenant2.auth.firma.at`).

### Admin locked out

[Recovery CLI](./recovery-cli) — a shell tool inside the container that bypasses the UI and writes directly to the database.

## Real-time updates

All admin lists refresh themselves automatically when another admin (or you in a second tab) changes something. This happens via SignalR push — no manual reload needed.
