# Database & Migrations

Cocoar.Auth uses Marten (PostgreSQL) with automatic schema management.

## Database Architecture

```
cocoar_auth_master   → Marten MasterTableTenancy registry
cocoar_auth_system   → System realm (users, roles, OAuth, events)
cocoar_auth_{slug}   → Per-tenant databases (one per realm)
```

## Schema Management

Marten manages its own schema with `AutoCreate.CreateOrUpdate`. On startup:

```csharp
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
```

This creates/updates all tables, indexes, functions, and projections. No manual migrations needed.

## Key Tables

| Table | Purpose |
|-------|---------|
| `mt_doc_applicationuser` | User documents (Identity) |
| `mt_doc_applicationrole` | Role documents (Identity) |
| `mt_doc_usersecuritydata` | Password hashes, authenticator keys |
| `mt_doc_userstate` | Inline projection for Identity validation |
| `mt_doc_rolestate` | Inline projection for role lookups |
| `mt_events` | Event store (all domain events) |
| `mt_streams` | Event stream metadata |
| `mt_doc_oauthapplicationstate` | OpenIddict client state |
| `mt_doc_oauthscopestate` | OpenIddict scope state |

## Tenant Database Provisioning

When a new realm is created via the admin API:

1. PostgreSQL `CREATE DATABASE` for the new tenant
2. Register in Marten's master table (`realms.mt_tenant_databases`)
3. Apply full Marten schema to the new database
4. Seed default data (OpenIddict scopes, login providers)
