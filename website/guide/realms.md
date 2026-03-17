# Multi-Tenancy / Realms

Cocoar.Auth uses a **realm** model for multi-tenancy. Each realm is a fully autonomous identity provider with its own database, users, roles, and OAuth configuration.

::: info Realm vs Tenant in the codebase
The user-facing term is **realm** (UI, API, URLs, documentation). The codebase uses **tenant** in the infrastructure layer (`TenantId`, `ITenantSessionFactory`, `MasterTableTenancy`) because that's what Marten and Wolverine call it. Same concept, two names: `TenantId` = realm slug.
:::

## How It Works

### URL-Based Realm Detection

The realm is determined from the URL path:

| URL Pattern | Realm | API Base |
|------------|-------|----------|
| `/api/...` | `system` | `/api` |
| `/realms/acme/api/...` | `acme` | `/realms/acme/api` |
| `/realms/corp/api/...` | `corp` | `/realms/corp/api` |

### RealmMiddleware

The `RealmMiddleware` runs before routing and:

1. Extracts the slug from `/realms/{slug}/...`
2. Validates the realm exists and is active (via `IRealmCache`)
3. Sets `HttpContext.Items["TenantId"]` and `HttpContext.Items["RealmSlug"]`
4. Rewrites `PathBase` so controllers see clean `/api/...` paths

For requests without `/realms/` prefix, the system realm is used (backward compatible).

### Database-per-Tenant

Marten's `MasterTableTenancy` maps each realm slug to its own PostgreSQL database:

```
cocoar_auth_master  → Tenant registry (slug → connection string)
cocoar_auth_system  → System realm data
cocoar_auth_acme    → Acme realm data
cocoar_auth_corp    → Corp realm data
```

The `IDocumentSession` is resolved per-request with the correct tenant ID:

```csharp
services.AddScoped<IDocumentSession>(sp =>
{
    var store = sp.GetRequiredService<IDocumentStore>();
    var tenantId = accessor.HttpContext?.Items["TenantId"] as string ?? "system";
    return store.LightweightSession(tenantId);
});
```

### Cookie Scoping

Auth cookies are scoped per realm to prevent cross-realm session leakage:

- System realm: cookie path = `/` (default)
- Tenant realm: cookie path = `/realms/{slug}`

The `cocoar:realm` claim is added to the user's identity during sign-in.

## Realm Lifecycle

### Creating a Realm

1. System admin calls `POST /api/admin/realms` with `{ slug, displayName, description }`
2. `RealmProvisioningService` creates a new PostgreSQL database
3. Registers the tenant in Marten's master table
4. Applies Marten schema (tables, indexes, functions)
5. Seeds default OpenIddict scopes and login providers
6. Stores realm metadata in the system tenant

### Realm Setup

New realms start with `needsSetup: true`. The first visitor to `/realms/{slug}/` is redirected to the setup flow where they create the realm's first admin account.

### The System Realm

The system realm is special only in that it can manage other realms via the `[SystemRealmOnly]` filter on the `RealmsAdminController`. All other functionality (users, roles, OAuth, 2FA) is identical.

## Frontend Realm Awareness

The Vue SPA is realm-agnostic by design:

```typescript
// composables/useRealmContext.ts
const match = window.location.pathname.match(/^\/realms\/([a-z][a-z0-9-]+)(\/|$)/);

export const realmContext = match
  ? { slug: match[1], apiUrl: `/realms/${match[1]}/api`, baseHref: `/realms/${match[1]}/`, isSystem: false }
  : { slug: 'system', apiUrl: '/api', baseHref: '/', isSystem: true };
```

- **API calls**: `http.ts` uses `realmContext.apiUrl` as base URL
- **Router**: `createWebHistory(realmContext.baseHref)` keeps URLs within the realm prefix
- **Sidebar**: Shows "Realms" menu only for system realm admins
- **Realm indicator**: Displays current realm slug in sidebar header
