# Realm Endpoints

Realm management is only possible from realms with
`CanManageTenants = true` (typically only the system realm).
Otherwise the endpoint returns **404** — not 403, because the
existence of realm CRUD must not be leaked.

Endpoints in `Cocoar.Auth.Api/Features/Admin/RealmsEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/realms` | `cocoar-auth:realm:read` |
| `GET` | `/api/admin/realms/{slug}` | `cocoar-auth:realm:read` |
| `POST` | `/api/admin/realms` | `cocoar-auth:realm:write` |
| `PATCH` | `/api/admin/realms/{slug}` | `cocoar-auth:realm:write` |
| `DELETE` | `/api/admin/realms/{slug}` | `cocoar-auth:realm:delete` (soft-delete = deactivate) |

## Create a realm

```http
POST /api/admin/realms
Content-Type: application/json

{
  "slug": "acme",
  "displayName": "Acme Corp",
  "description": "Acme Corporation Identity",
  "domains": ["acme.example.com"],
  "canManageTenants": false
}
```

### What happens

1. **Slug validation**: regex `^[a-z][a-z0-9-]{1,61}[a-z0-9]$`, no
   reserved word (`system`, `health`, `swagger`, `api`, `connect`,
   ...)
2. **Create PostgreSQL DB** (raw SQL):
   `CREATE DATABASE cocoar_auth_next_acme`
3. **Register in Marten tenancy**:
   `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`
4. **Apply Marten schema** (tables, indexes, functions)
5. **OAuthRealmSeeder.SeedAsync** seeds the new DB:
   - 5 default scopes (`openid`, `email`, `profile`, `roles`,
     `offline_access`)
   - Internal login provider
6. **AuthorizationSeeder** seeds 3 default roles (System Admin, User
   Manager, Viewer)
7. **Realm document in `IGlobalStore`** (master DB, schema `global`)
8. **RealmCache.Invalidate()** — the next request loads it fresh

### Response

```json
{
  "id": "...",
  "slug": "acme",
  "displayName": "Acme Corp",
  "description": "Acme Corporation Identity",
  "domains": ["acme.example.com"],
  "canManageTenants": false,
  "isActive": true,
  "createdAt": "2026-04-29T10:00:00Z"
}
```

## Edit a realm

```http
PATCH /api/admin/realms/{slug}
Content-Type: application/json

{
  "displayName": "Acme Corporation",
  "description": "Updated",
  "domains": ["acme.example.com", "auth.acme.com"]
}
```

`Slug` is immutable. `CanManageTenants` cannot be changed via PATCH —
that would enable an authorization escalation.

## Deactivate a realm

```http
PATCH /api/admin/realms/{slug}
{ "isActive": false }
```

`RealmCache` filters on `IsActive = true` — all requests to the realm
domain land at `404`. Data is preserved.

::: danger System realm
The system realm must not be deactivated — the endpoint blocks it.
:::

## Hard-delete a realm

::: warning Not implemented
Current state: only soft-delete (deactivation). The tenant DB is not
dropped. This is an open roadmap item — the Wolverine durability
agent has to be shut down cleanly, the tenant has to be removed from
`mt_tenant_databases`, all sessions have to be invalidated, and
finally the DB has to be dropped.
:::

## Setup flow for a new realm

After `POST /api/admin/realms`:

1. Open the browser on the new realm domain (e.g.
   `https://acme.example.com/`)
2. The frontend calls `GET /api/setup/status` →
   `{ needsSetup: true }`
3. The frontend redirects to `/setup`
4. The user creates the first-time admin
5. Auto-login as system admin in the new realm

The first user is automatically placed into the "System Admin"
default group with `BoundTo: ["*"]`; the role carries `realm:admin`.

## Realm data model

The `Realm` document
(`src/dotnet/Cocoar.Auth.Domain/Realms/Realm.cs`):

```csharp
public class Realm
{
    public Guid Id { get; set; }
    public string Slug { get; set; }              // = TenantId, immutable
    public string DisplayName { get; set; }
    public string? Description { get; set; }
    public string[] Domains { get; set; }         // Host-header matches
    public bool CanManageTenants { get; set; }    // may run realm CRUD
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

Lives in `IGlobalStore` (master DB, schema `global`) — not in the
tenant store, because that would create a chicken-and-egg problem.
