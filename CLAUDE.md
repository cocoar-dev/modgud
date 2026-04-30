# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

Cocoar.Auth is the central Identity Provider for all Cocoar SaaS apps.
Cookie-based login + full OAuth 2.0 / OIDC server (OpenIddict 7), built on
TimeToDo's `Authentication` + `Authorization` slices and extended with
IdP-specific concerns: multi-realm tenancy, OAuth aggregate admin,
sessions with device tracking, GDPR self-service, granular per-resource
permission gating.

## Layout

```
src/
├── dotnet/           # Backend — ASP.NET Core 10 + Marten + Wolverine + OpenIddict
└── frontend-vue/     # Frontend — Vue 3 + @cocoar/vue-ui + AG Grid + SignalR
```

Both have their own README with project-level details.

The pre-cutover legacy codebase is preserved at git tag `legacy-final`.

## Backend essentials

**Stack:** .NET 10, Marten 8.x (multi-tenant, master-table strategy:
each realm is a physical PostgreSQL database `cocoar_auth_next_<slug>`),
Wolverine 5.x (CQRS + outbox), OpenIddict 7.x, ErrorOr, Mapperly,
Cocoar.JsEval (TS → LINQ for membership scripts), Cocoar.SignalARRR.

**Architecture:**
- TimeToDo Authentication slice (`Cocoar.Auth.Authentication`) — login,
  register, 2FA, magic link, passkey, email OTP, OIDC external auth,
  change requests, sessions, GDPR, recovery CLI
- TimeToDo Authorization slice (`Cocoar.Auth.Authorization`) — groups
  (incl. JsEval-based auto-membership scripts), roles, permissions,
  ResourceRegistry. Pure RBAC — row-level access (ABAC) stays in the
  consuming app, see `website/concepts/abac.md`.
- IdP-specific layers added on top: OAuth aggregates, OpenIddict
  Marten stores, Realm domain + provisioning + middleware, Sessions
  with UAParser, GDPR Marten masking + ArchiveStream

**Tenancy:** every `IDocumentSession` injection is automatically
tenant-scoped via a custom Marten `ISessionFactory`
(`TenantedSessionFactory`) that reads `HttpContext.Items["TenantId"]`
set by `RealmMiddleware`. Background services fall back to the `system`
tenant. Adding a realm provisions a fresh DB and seeds default
OAuth scopes + Internal login provider.

**Permissions:** `<app>:<resource>:<action>` style (e.g.
`cocoar-auth:user:read`, `cocoar-auth:oauth-client:write`,
`timetodo:todo:write`). Three bypass tiers: `<app>:<resource>:admin`
(resource-wide within an app), `<app>:admin` (app-wide),
`realm:admin` (realm-wide emergency exit, the System Admin role).
Endpoints gate via `.RequiresPermission("...")` (extension on
`RouteHandlerBuilder`).

## Frontend essentials

**Stack:** Vue 3 (`<script setup>`), Pinia 3, Vue Router 5, Vite 8,
Tailwind 4, `@cocoar/vue-ui` (CoarButton, CoarSidebar, CoarMenu, ...),
`@cocoar/vue-data-grid` (CoarGridBuilder over AG Grid),
`@cocoar/signalarrr`, `@cocoar/vue-localization`,
`@cocoar/vue-fragment-parser` (URL-fragment routed modals),
`@cocoar/vue-script-editor` (Monaco for membership scripts).

**Patterns:**
- `useUI()` for header/footer/content context
- `useEntityService()` for generic CRUD + auto-resubscribe to SignalR
  change streams
- `useHttpClient()` immutable fluent builder
- `useModal()` programmatic + `useRoutedModals()` URL-fragment modals
- Sidebar: per-resource gating in `views/admin/AdminView.vue` —
  declarative `requirePermissions[]` per item, mirrors backend strings
  exactly

## Build & run

```bash
# Backend (port 9099)
cd src/dotnet
dotnet build
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE cocoar_auth_next;"  # first time
cd Cocoar.Auth.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile

# Frontend (port 4300)
cd src/frontend-vue
pnpm install
pnpm dev

# First-time setup at http://localhost:4300/setup
# Default admin in dev: admin / ABC12abc!
```

## Testing

- `Cocoar.Auth.Api.Tests` — integration tests on Testcontainers + PostgreSQL
- `dotnet test` from `src/dotnet`
- The TimeToDo test patterns (xUnit collections, shared Postgres
  container, per-class DB isolation) carry over

## Configuration

`src/dotnet/Cocoar.Auth.Api/data/configuration.json` (committed defaults)
+ `configuration.local.json` (gitignored). Bound via Cocoar.Configuration
v5 layered binding. Settings types: `StartUpConfiguration`, `AppSettings`,
`EmailConfiguration`, `MagicLinkConfiguration`, `EmailOtpConfiguration`,
`OpenIddictSettings`.

## When in doubt

- TimeToDo source is the canonical pattern reference: `C:\git\cocoar\timetodo`
- TimeToDo doc on the patterns: `C:\git\cocoar\timetodo\website\technik\`
  and `\konzept\`
- The pre-cutover legacy is at `git checkout legacy-final` for any
  historical lookup ("how did the old IdP do X?")
