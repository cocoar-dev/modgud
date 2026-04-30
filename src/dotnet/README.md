# Cocoar.Auth — Backend

ASP.NET Core 10 Identity Provider built on top of TimeToDo's
`Authentication` + `Authorization` slices, extended with the IdP-specific
concerns (Multi-Realm, OpenIddict, OAuth aggregates, GDPR, sessions,
granular gating).

## Solution layout

| Project | Purpose |
|---|---|
| `Cocoar.Auth.Api` | ASP.NET Core host, Minimal API endpoints, Wolverine + Marten wiring, OAuth/Realm/Sessions/GDPR endpoints |
| `Cocoar.Auth.Authentication` | Login, register, 2FA, magic link, passkey, email OTP, external OIDC, change requests, sessions, GDPR, recovery CLI, IdP-config |
| `Cocoar.Auth.Authorization` | Groups (incl. JsEval-based auto-membership scripts), roles, permissions, principals, ResourceRegistry, RequiresPermission filter. Pure RBAC — no row-level ABAC, that stays in the consuming app. |
| `Cocoar.Auth.Application` | DTOs, application services (OAuthAdminService, LoginProviderService, etc.) |
| `Cocoar.Auth.Domain` | Aggregates (OAuth, LoginProviders, Realms), domain events, value objects |
| `Cocoar.Auth.Infrastructure` | Marten setup (master-table multi-tenancy), TenantedSessionFactory, OpenIddict Marten stores, RealmCache + RealmProvisioningService |
| `Common` | BuildingBlocks (event dispatcher helpers) |
| `Cocoar.Auth.Api.Tests` | Integration tests (Testcontainers + PostgreSQL) |

## Build & run

```bash
cd src/dotnet
dotnet build

# Reset dev DB
docker exec cocoar-postgres psql -U postgres -c "DROP DATABASE IF EXISTS cocoar_auth_next;"
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE cocoar_auth_next;"

# Run the API
cd Cocoar.Auth.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
# → http://localhost:9099

# Tests
cd src/dotnet
dotnet test
```

## Configuration

`Cocoar.Auth.Api/data/configuration.json` (committed defaults) +
`configuration.local.json` (gitignored, local overrides). Cocoar.Configuration
v5 layered binding.

## History

This codebase replaced an earlier event-sourced IdP that lived in the same
folder. The pre-cutover legacy is preserved at the `legacy-final` git tag.
