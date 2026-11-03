# Modgud — Backend

ASP.NET Core 10 Identity Provider. Two reusable vertical slices
(`Modgud.Authentication` + `Modgud.Authorization`) sit underneath the
IdP-specific layers (multi-realm tenancy, OpenIddict, OAuth aggregates,
GDPR, sessions, granular gating).

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

Follow the [workspace safety rules](../../AGENTS.md) and
[development preflight](../../docs/contribute/developing-locally.md) first:
existing `postgres-dev`, exclusive `127.0.0.1:5433` and dedicated volume,
private development credentials, a work-owned database and unused loopback
API port. Verify the **resolved** startup configuration before bootstrap.
Do not copy shared connection strings or reset shared databases.

From the repository root, in your verified private development shell:

```bash
: "${DbSettings__ConnectionString:?Set and verify the private postgres-dev target first}"
: "${AppUrl:?Set and verify an unused loopback API address first}"
cd src/dotnet
dotnet build
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
```

For pure units, from the repository root:

```bash
dotnet test src/dotnet/Modgud.Tests.Unit/Modgud.Tests.Unit.csproj --no-restore
```

API tests need the separately verified `MODGUD_TEST_POSTGRES_CONNECTION_STRING`
opt-in and a fresh `modgud_review_*` database on Dev5433. Use the filtered
example in the preflight guide; do not default to Testcontainers or run
unreviewed fixture/container lifecycles in this shared workspace.

## Configuration

`Modgud.Api/data/configuration.json` (committed defaults) +
`configuration.local.json` (gitignored, local overrides). Cocoar.Configuration
v6 layered binding; environment overrides win over both files.

## History

This codebase replaced an earlier event-sourced IdP that lived in the same
folder. The pre-cutover legacy is preserved at the `legacy-final` git tag.
