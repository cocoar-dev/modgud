# Overview

cocoar.auth is a self-contained multi-tenant Identity Provider
(comparable to Keycloak, Zitadel, or Authentik). ASP.NET Core 10,
Marten 9, OpenIddict 7, Vue 3.

## What it does

- **Authentication** — cookie-based sessions with password, TOTP,
  email OTP, passkey/FIDO2, magic link, OIDC external login
- **OAuth 2.0 / OIDC server** — full authorization server via
  OpenIddict (Authorization Code + PKCE, Client Credentials, Refresh
  Token; reference + JWT tokens)
- **Multi-tenancy** — database-per-realm via Marten
  `MasterTableTenancy`; domain-based routing
- **User & group management** — RBAC with per-resource gating, app
  scoping via Group.BoundTo. ABAC deliberately stays the consuming
  app's responsibility (see [Concepts → ABAC](/concepts/abac))
- **GDPR self-service** — data export (Article 20), account deletion
  with confirmation token, Marten data masking
- **Sessions with device tracking** — UAParser-based, self-service
  revoke, log out everywhere

## Structure

cocoar.auth sits on two vertical slices, pulled in as C# projects:

- [`Cocoar.Auth.Authentication`](/authentication-slice/) — login,
  2FA, OIDC, GDPR, sessions
- [`Cocoar.Auth.Authorization`](/authorization-slice/) — groups,
  roles, permissions

On top of that lives the IdP-specific code:

- **`Cocoar.Auth.Domain`** — Realm, OAuth, LoginProvider aggregates
- **`Cocoar.Auth.Infrastructure`** — OpenIddict Marten stores,
  tenancy plumbing, realm cache + provisioning, Wolverine handlers
- **`Cocoar.Auth.Application`** — DTOs, service interfaces
- **`Cocoar.Auth.Api`** — Minimal API endpoints, middleware,
  setup bootstrap, SignalR hub

Plus the frontend at `src/frontend-vue/`.

## Tech stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 10 (Minimal APIs) |
| CQRS | Wolverine 6 (mediator + outbox) |
| Persistence | Marten 9 (document DB + event store on PostgreSQL) |
| OAuth/OIDC | OpenIddict 7 with Marten stores |
| Identity | ASP.NET Core Identity + EventSourcedUserStore |
| Realtime | SignalR + Cocoar.SignalARRR (typed RPC) |
| Frontend | Vue 3 + Pinia 3 + Vite 8 + Tailwind 4 |
| Components | @cocoar/vue-ui, @cocoar/vue-data-grid, ... |
| Testing | xUnit + Testcontainers (PostgreSQL in Docker), Playwright (E2E) |

## Key design decisions

### Reference tokens by default

All access tokens are **reference tokens** by default — opaque strings
stored server-side in `OpenIddictTokenDocument`. This is the primary
reason to build a custom IdP rather than rent an existing cloud IdP:
instant revocation and no long-lived JWTs in browsers. Per client this
can be switched to JWT when performance-critical.

### Database-per-realm

Each realm has its own PostgreSQL database. Marten
`MasterTableTenancy` resolves the connection per request — no
`tenant_id` columns in joins, no shared tables. Maximum isolation. The
price is having more DBs to operate — at typical scale (single- to
double-digit number of realms per installation) this is entirely
manageable.

### Domain-based realm routing

Realms are resolved via the Host header, not via URL path. Each realm
has its own domain (`acme.example.com`, `system.example.com`). That
makes OIDC issuers, cookies, and frontend builds work without
path-prefix acrobatics.

### TimeToDo slices as the basis

The Authentication and Authorization slices are pulled in as C#-project
copies straight from TimeToDo. Updates don't flow automatically —
whoever changes something in cocoar.auth changes their copy. This
gives stability against upstream breaking changes and allows
app-specific extensions (e.g. more resources in the Authorization
slice).

### Granular per-resource gating

Permissions follow the `<app>:<resource>:<action>` format
(`cocoar-auth:user:read`, `timetodo:todo:write`, …). Every endpoint
and every sidebar entry checks the same string. Three bypass tiers:
`<app>:<resource>:admin` (resource-wide within an app), `<app>:admin`
(app-wide), `realm:admin` (realm-wide emergency exit). This scales
cleanly as Cocoar.Auth hosts additional consuming apps.
