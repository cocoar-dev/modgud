# Modgud — Backend

ASP.NET Core 10 Identity Provider built on top of TimeToDo's
`Authentication` + `Authorization` slices, extended with the IdP-specific
concerns (Multi-Realm, OpenIddict, OAuth aggregates, GDPR, sessions,
granular gating).

## Solution layout

| Project | Purpose |
|---|---|
| `Modgud.Api` | ASP.NET Core host, Minimal API endpoints, Wolverine + Marten wiring, OAuth/Realm/Sessions/GDPR endpoints |
| `Modgud.Authentication` | Login, register, 2FA, magic link, passkey, email OTP, external OIDC, change requests, sessions, GDPR, recovery CLI, IdP-config |
| `Modgud.Authorization` | Groups (incl. JsEval-based auto-membership scripts), roles, permissions, principals, ResourceRegistry, RequiresPermission filter. Pure RBAC — no row-level ABAC, that stays in the consuming app. |
| `Modgud.Application` | DTOs, application services (OAuthAdminService, LoginProviderService, etc.) |
| `Modgud.Domain` | Aggregates (OAuth, LoginProviders, Realms), domain events, value objects |
| `Modgud.Infrastructure` | Marten setup (master-table multi-tenancy), TenantedSessionFactory, OpenIddict Marten stores, RealmCache + RealmProvisioningService |
| `Common` | BuildingBlocks (event dispatcher helpers) |
| `Modgud.Api.Tests` | Integration tests (Testcontainers + PostgreSQL) |

## Build & run

```bash
cd src/dotnet
dotnet build

# Reset dev DB
docker exec cocoar-postgres psql -U postgres -c "DROP DATABASE IF EXISTS <master-db>;"
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE <master-db>;"

# Run the API
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
# → http://localhost:9099

# Tests
cd src/dotnet
dotnet test
```

## Configuration

`Modgud.Api/data/configuration.json` (committed defaults) +
`configuration.local.json` (gitignored, local overrides). Cocoar.Configuration
v5 layered binding.

## History

This codebase replaced an earlier event-sourced IdP that lived in the same
folder. The pre-cutover legacy is preserved at the `legacy-final` git tag.
