# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

Modgud is a multi-tenant Identity Provider — cookie-based login plus
a full OAuth 2.0 / OpenID Connect server (OpenIddict 7) with
multi-realm tenancy, OAuth client/scope/API admin, sessions with
device tracking, GDPR self-service, and granular per-resource
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

**Stack:** .NET 10, Marten 9.x (multi-tenant, master-table strategy:
each realm is a physical PostgreSQL database `<master-db>_<slug>`),
Wolverine 6.x (CQRS + outbox), OpenIddict 7.x, ErrorOr, Mapperly,
Cocoar.JsEval (TS → LINQ for membership scripts), Cocoar.SignalARRR.

**Architecture:**
- `Modgud.Authentication` slice — login, register, 2FA, magic link,
  passkey, email OTP, OIDC external auth, change requests, sessions,
  GDPR, recovery CLI
- `Modgud.Authorization` slice — groups (incl. JsEval-based
  auto-membership scripts), roles, permissions, ResourceRegistry.
  Pure RBAC — row-level access (ABAC) stays in the consuming app,
  see `docs/concepts/abac.md`.
- IdP-specific layers added on top: OAuth aggregates, OpenIddict
  Marten stores, Realm domain + provisioning + middleware, Sessions
  with UAParser, GDPR Marten masking + ArchiveStream

**Tenancy:** every `IDocumentSession` injection is automatically
tenant-scoped via a custom Marten `ISessionFactory`
(`TenantedSessionFactory`) that reads `HttpContext.Items["TenantId"]`
set by `RealmMiddleware`. Background services fall back to the `system`
tenant. Adding a realm provisions a fresh DB and seeds default
OAuth scopes + Internal login provider.

**Permissions:** `<resource>:<action>` style (e.g. `user:read`, `oauth-client:write`) — the app context is implicit, because a role is FK'd to an App (the IdP itself for in-process gates, the authenticated resource server for distribution-API calls). Two bypass tiers around an exact match: `realm:admin` (realm-wide emergency exit, the System Admin role) and `<resource>:admin` (resource-wide). Evaluation order is `realm:admin` → exact match → `<resource>:admin` (see `PermissionEvaluator` in `Modgud.Permissions.Abstractions`). Endpoints gate via `.RequiresPermission("...")` (extension on `RouteHandlerBuilder`).

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
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE <master-db>;"  # first time
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile

# Frontend (port 4300)
cd src/frontend-vue
pnpm install
pnpm dev

# First-time setup: there is NO anonymous /setup — create the first admin with the
# recovery CLI (`recover bootstrap-admin`); see docs/getting-started/first-time-setup.
# Default admin in dev: admin / ABC12abc!
```

## Testing

- `Modgud.Api.Tests` — integration tests on Testcontainers + PostgreSQL
- `dotnet test` from `src/dotnet`
- xUnit collections + a shared Postgres container + per-class DB
  isolation keep parallelism safe.

## Configuration

`src/dotnet/Modgud.Api/data/configuration.json` (committed defaults) + `configuration.local.json` (gitignored). Bound via Cocoar.Configuration v6 layered binding (env-var binding is case-insensitive). Settings types: `StartUpConfiguration`, `AppSettings`, `EmailConfiguration`, `MagicLinkConfiguration`, `EmailOtpConfiguration`, `ObservabilitySettings`, `OpenIddictSettings`.

## When in doubt

- The pre-cutover legacy snapshot is at `git checkout legacy-final` for
  any historical lookup ("how did the old IdP do X?").
