# Tenancy: database-per-realm, master/system split

**Status:** Accepted — verified against current code 2026-06-13 (`TenantedSessionFactory.cs`, `RealmProvisioningService.cs`). Originally drafted from internal notes; the claims below are now confirmed in code. · **Decided:** 2026-04-29

## Context

Modgud is a multi-tenant IdP where a "tenant" is a **realm** (an isolated identity domain with its own users, groups, OAuth clients, login providers, branding). Tenant data must be strongly isolated, with per-tenant backup/restore and a clean blast-radius boundary.

## Decision

- **Each realm is a physical PostgreSQL database** named `{mainDb}_{slug}` (Marten multi-tenant **master-table strategy**, `MultiTenantedDatabasesWithMasterDatabaseTable`). Adding a realm `CREATE DATABASE`s a fresh DB and seeds its defaults; `adopt-tenant` registers an already-existing `{master}_{slug}` DB without creating it.
- **Every `IDocumentSession` is automatically tenant-scoped** via a custom Marten `ISessionFactory` (`TenantedSessionFactory`): it resolves the tenant from the ambient `TenantContext` then `HttpContext.Items["TenantId"]` (set by `RealmMiddleware`, Host → realm). With no request context it falls back to the `system` tenant — but a tenant-scoped **write** during an HTTP request with no resolved realm is **refused loudly** (anti-silent-fallback guard).
- **The master DB is pure control-plane infrastructure and holds no tenant content**; the `system` realm has its **own** database (`{master}_system`).
- **The control-plane role is transferable** (persisted `Realm.IsControlPlane` flag) — a single-tenant deployment can later hand cross-realm administration to another realm.

## Alternatives considered (and rejected)

- **Shared DB with a tenant-id column:** rejected — weaker isolation, easy cross-tenant leak via a missed filter, no per-tenant backup/restore or drop.
- **Schema-per-tenant in one DB:** weaker blast-radius boundary than separate databases; not chosen.

## Consequences

- Strong isolation; per-realm backup/restore and drop are trivial.
- **To watch:** connection-pool budget scales with realm count; the boot-time schema migration is O(number of realms) and not currently resumable.

## References

- Code (verified 2026-06-13): `Modgud.Infrastructure/Persistence/Tenancy/TenantedSessionFactory.cs`; `Modgud.Infrastructure/Realms/RealmProvisioningService.cs` (*"Tenant DBs are PostgreSQL databases named `{mainDb}_{slug}`"*); `MartenConfiguration.UseMasterTableMultiTenancy`. ADR-0002 (per-realm public origin builds on this).
