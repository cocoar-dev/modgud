# Realm Architecture - Multi-Tenancy for Modgud

## Concept

A **Realm** is a fully isolated identity provider instance. Each realm has its own users, roles,
clients, keys, sessions, tokens, and login providers. Realms share the same application runtime
and PostgreSQL server, but each realm gets its own **separate database**.

This is equivalent to Keycloak's Realm concept, but with stronger isolation
(Keycloak uses a single database with discriminator columns).

---

## Why Database-per-Tenant

| Benefit | Description |
|---|---|
| **True isolation** | No query filter bugs can leak data between tenants |
| **GDPR compliance** | `DROP DATABASE` = complete data erasure, no residual rows |
| **Migration** | Export a tenant's DB, import it elsewhere (cloud, on-prem) |
| **Customer offboarding** | Drop the database, done |
| **Backup/Restore** | Per-tenant backups without affecting others |
| **Performance** | Tenants can't affect each other's query performance |
| **Schema evolution** | Migrate tenants independently if needed |

**Note:** Marten does NOT support schema-per-tenant (GitHub Issue #752, closed).
Database-per-tenant on the same PostgreSQL server is the supported approach.

---

## URL Structure

The realm name is always the **first URL segment**.

```
/{realm}/.well-known/openid-configuration
/{realm}/connect/authorize
/{realm}/connect/token
/{realm}/connect/userinfo
/{realm}/connect/introspect
/{realm}/connect/revoke
/{realm}/connect/logout

/{realm}/api/auth/login
/{realm}/api/auth/logout
/{realm}/api/auth/register
/{realm}/api/auth/me

/{realm}/api/admin/users
/{realm}/api/admin/roles
/{realm}/api/admin/clients
/{realm}/api/admin/scopes
/{realm}/api/admin/api-resources
/{realm}/api/admin/login-providers
```

Extensible for future protocols:
```
/{realm}/protocol/saml/...
```

---

## What Is Isolated Per Realm (= Per Database)

| Resource | Isolated | Notes |
|---|---|---|
| **Users & Credentials** | Yes | A user exists only in one realm |
| **Roles** | Yes | Realm-specific roles |
| **Clients (OAuth Applications)** | Yes | Each realm has its own registered apps |
| **Scopes** | Yes | OpenID/custom scopes per realm |
| **API Resources** | Yes | Per realm |
| **Login Providers** | Yes | Each realm configures its own external IdPs |
| **Signing Keys** | Yes | Each realm signs tokens with its own keys |
| **Sessions** | Yes | SSO only within a realm |
| **Tokens** | Yes | Issuer = `https://host/{realm}` |
| **Event Streams** | Yes | Separate database = separate event store |
| **Projections** | Yes | Each DB has its own projection state |
| **Audit Logs** | Yes | Events are per-database |

**Shared across realms:**
- Application runtime (single deployed instance)
- PostgreSQL server (separate databases on same server)
- Master database (realm registry)
- System admin (super-admin manages all realms)

---

## Default Realm

On first start, a **default realm** is created (name: `"default"`).
This realm contains the initial admin user and is always present.

Every request MUST have a realm context. There is no "realm-less" operation.

Database naming convention: `modgud_{realm_name}`
- `modgud_default` (default realm)
- `modgud_acme` (tenant "acme")
- `modgud_globex` (tenant "globex")

---

## Technical Implementation with Marten

### Database Architecture

```
PostgreSQL Server
├── modgud_master     ← Master DB: realm registry, system config
├── modgud_default    ← Default realm: users, roles, clients, events...
├── modgud_acme       ← Acme realm: users, roles, clients, events...
└── modgud_globex     ← Globex realm: users, roles, clients, events...
```

### Marten Configuration: Master Table Approach

```csharp
services.AddMarten(opts =>
{
    opts.MultiTenantedDatabasesWithMasterDatabaseTable(x =>
    {
        x.ConnectionString = masterConnectionString;
        x.SchemaName = "realms";
        x.AutoCreate = AutoCreate.CreateOrUpdate;
        x.ApplicationName = "Modgud";

        // Pre-register default realm
        x.RegisterDatabase("default", defaultRealmConnectionString);
    });

    opts.Events.TenancyStyle = TenancyStyle.Conjoined;
    // Note: TenancyStyle.Conjoined is required even with database-per-tenant
    // because Marten uses it internally for event stream routing
})
.IntegrateWithWolverine(x =>
{
    x.MainDatabaseConnectionString = masterConnectionString;
});
```

This creates a table `realms.mt_tenant_databases` in the master DB:

| tenant_id | connection_string |
|---|---|
| default | Host=localhost;Database=modgud_default;... |
| acme | Host=localhost;Database=modgud_acme;... |

### Dynamic Realm Provisioning

```csharp
// Create a new realm at runtime
var tenancy = (MasterTableTenancy)store.Options.Tenancy;
var connectionString = $"Host=localhost;Database=modgud_{realmName};...";

// Register in master table (Marten auto-creates the database on same server)
await tenancy.AddDatabaseRecordAsync(realmName, connectionString);

// Marten auto-applies schema to new database
// Async daemon auto-starts for new database
// Seed default data (admin role, internal login provider, default scopes)
```

### Realm Resolution Middleware

```csharp
public class RealmMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var realmName = segments[0].ToLowerInvariant();

        // Set tenant for Marten session resolution
        // Marten's built-in middleware will use this
        context.Items["TenantId"] = realmName;

        // Rewrite path to remove realm segment for routing
        // /{realm}/connect/authorize → /connect/authorize
        var remainingPath = "/" + string.Join("/", segments.Skip(1));
        context.Request.Path = new PathString(remainingPath);

        await next(context);
    }
}
```

### Session Usage (No Code Changes Needed)

With Marten's database-per-tenant, sessions are automatically scoped:

```csharp
// Marten resolves the tenant from HttpContext
// and opens the session against the correct database
public class MyRepository(IDocumentSession session)
{
    // This session is already scoped to the current realm's database
    // No ForTenant() calls needed if middleware sets the tenant correctly
}
```

### OpenIdDict Integration

Each realm has its own issuer:
```
Realm "default":  issuer = https://auth.example.com/default
Realm "acme":     issuer = https://auth.example.com/acme
```

OpenIdDict's Marten stores use `IDocumentSession` which is already realm-scoped.
All OpenIdDict data (applications, authorizations, tokens, scopes) is automatically
in the correct realm's database.

### Signing Keys Per Realm

Each realm needs its own signing keys. Stored in the realm's database:
- Generated on realm creation
- OpenIdDict can manage key rotation per realm
- Token from realm A is invalid in realm B (different keys, different issuer)

### Async Daemon

- Marten starts a **separate daemon instance per database** automatically
- New realm databases are detected and daemon spins up with **zero downtime**
- Each realm's projections run independently
- Projection rebuilds can target a single realm: `dotnet run -- marten-rebuild --tenant acme`

### Wolverine

- Separate message storage tables in each tenant database
- Master database handles Wolverine's own durable messaging
- Cross-tenant messaging: `await bus.InvokeForTenantAsync("acme", command)`
- Transactional middleware respects tenant context automatically

---

## Implementation Phases

### Phase 1: Foundation (Non-Breaking)

**Goal: Enable database-per-tenant with a single "default" realm. Everything works as before.**

- [ ] Create master database (`modgud_master`) with realm registry
- [ ] Configure Marten with `MultiTenantedDatabasesWithMasterDatabaseTable`
- [ ] Register "default" realm pointing to existing database
- [ ] Add RealmMiddleware that extracts realm from URL and sets tenant context
- [ ] Ensure Marten's scoped `IDocumentSession` uses tenant context from middleware
- [ ] Update Wolverine integration with master database connection
- [ ] Adapt `ITenantProvider` in common.internal to read from `HttpContext.Items["TenantId"]`
- [ ] All existing tests pass (they use "default" realm implicitly)
- [ ] OpenIdDict issuer becomes `/{realm}` based

**Data migration:** Existing database becomes `modgud_default`. Master DB is new.

### Phase 2: URL Routing

**Goal: All URLs include realm segment.**

- [ ] All routes prefixed with `/{realm}/`
- [ ] Frontend sends requests with realm prefix
- [ ] `.well-known/openid-configuration` returns realm-specific endpoints
- [ ] Login page URL includes realm
- [ ] Frontend reads realm from URL on load

### Phase 3: Realm Management API

**Goal: System admin can create/manage realms.**

- [ ] Realm management endpoints (system-level, not realm-scoped):
  - `GET    /system/api/realms` - list all realms
  - `POST   /system/api/realms` - create realm
  - `GET    /system/api/realms/{name}` - get realm details
  - `DELETE /system/api/realms/{name}` - delete realm (drops database)
- [ ] Realm creation provisions: new database, schema, seed data, signing keys
- [ ] System admin role (separate from realm admin)
- [ ] Realm admin seeding (first admin user per realm)

### Phase 4: Realm-Specific Configuration

**Goal: Each realm can have different settings.**

- [ ] Per-realm authentication policies (password rules, MFA requirements)
- [ ] Per-realm token lifetimes
- [ ] Per-realm branding/theming
- [ ] Per-realm login providers (already isolated by database)

### Phase 5: Frontend

**Goal: Single SPA works for all realms.**

- [ ] Realm name extracted from URL on app init
- [ ] All API calls prefixed with realm
- [ ] Login page shows realm-specific branding (if configured)
- [ ] System admin UI for realm management

---

## Files That Need Changes (Phase 1)

### New Files
| File | Purpose |
|---|---|
| `RealmMiddleware.cs` | Extracts realm from URL, sets tenant context, rewrites path |
| `RealmSeeder.cs` | Seeds default data when new realm is created |

### Modified Files
| File | Change |
|---|---|
| `Infrastructure/DependencyInjection.cs` | `MultiTenantedDatabasesWithMasterDatabaseTable` config |
| `Api/Program.cs` | Register middleware, master DB connection, Wolverine integration |
| `Infrastructure/OpenIddict/OpenIddictExtensions.cs` | Realm-specific issuer |
| `Api/Controllers/AuthorizationController.cs` | Dynamic issuer from realm context |
| `Tests/Infrastructure/ModgudWebApplicationFactory.cs` | Test with "default" tenant |
| `Tests/Infrastructure/SharedPostgresFixture.cs` | Provision test tenant database |

### common.internal
| File | Change |
|---|---|
| `TenantProvider/HttpContextTenantProvider.cs` | Read from `HttpContext.Items["TenantId"]` |
| `Cocoar.Marten.Extensions/MContext.cs` | Uncomment TenantId |
| `Cocoar.Wolverine.Extensions.Http/HttpMessageContext.cs` | Uncomment WithTenant() |

---

## Key Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Isolation level | Database-per-tenant | True isolation, GDPR, migration, backup/restore |
| Marten approach | MasterTableTenancy | Dynamic provisioning, auto-schema, daemon support |
| Default realm name | `"default"` | Simple, clear |
| System admin | Separate from realm admin | System admin manages realms, realm admin manages users/clients |
| URL structure | `/{realm}/...` | Clean, no `/realms/` prefix needed |
| Frontend | Single SPA, realm from URL | No per-realm deployment needed |
| Signing keys | Per realm, stored in realm DB | Token isolation, standard practice |

---

## Operational Benefits

```
# Backup a single tenant
pg_dump modgud_acme > acme_backup.sql

# Restore a tenant
psql -c "CREATE DATABASE modgud_acme"
psql modgud_acme < acme_backup.sql

# Delete a tenant completely (GDPR right to erasure)
psql -c "DROP DATABASE modgud_acme"

# Migrate a tenant to another server
pg_dump modgud_acme | psql -h other-server modgud_acme

# Per-tenant projection rebuild
dotnet run -- marten-rebuild --tenant acme
```

---

## Risk Assessment

| Risk | Severity | Mitigation |
|---|---|---|
| Connection pool exhaustion (many tenants) | Medium | PgBouncer, connection pooling per tenant |
| Async daemon resource usage | Medium | One daemon per DB, monitor resource usage |
| Test complexity | Medium | Default tenant in tests, shared fixture provisions tenant DB |
| OpenIdDict multi-DB quirks | Medium | Test thoroughly with realm-scoped stores |
| Migration of existing data | Low | Existing DB becomes "default" realm, master DB is new |

---

## References

- Marten Multi-Tenancy Docs: https://martendb.io/configuration/multitenancy
- Marten Master Table Tenancy: https://jeremydmiller.com/2024/02/21/dynamic-tenant-databases-in-marten/
- Wolverine Multi-Tenancy: https://wolverinefx.net/guide/durability/marten/multi-tenancy
- Keycloak Realm Architecture (pattern reference)
- Existing implementation: Tellify (`C:\git\cocoar\tellify\`)
- Existing infrastructure: common.internal (`C:\git\cocoar\common.internal\code\`)
