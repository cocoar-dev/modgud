# Realm-Endpoints

Realm-Verwaltung ist nur möglich aus Realms mit
`CanManageTenants = true` (typischerweise nur der System-Realm).
Sonst returnt der Endpoint **404** — nicht 403, weil die Existenz von
Realm-CRUD nicht geleakt werden soll.

Endpoints in
`Cocoar.Auth.Api/Features/Admin/RealmsEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/realms` | `realm:read` |
| `GET` | `/api/admin/realms/{slug}` | `realm:read` |
| `POST` | `/api/admin/realms` | `realm:write` |
| `PATCH` | `/api/admin/realms/{slug}` | `realm:write` |
| `DELETE` | `/api/admin/realms/{slug}` | `realm:delete` (Soft-Delete = Deactivate) |

## Realm anlegen

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

### Was passiert

1. **Slug-Validation**: Regex `^[a-z][a-z0-9-]{1,61}[a-z0-9]$`, kein
   Reserved-Word (`system`, `health`, `swagger`, `api`, `connect`, ...)
2. **PostgreSQL-DB anlegen** (raw SQL): `CREATE DATABASE cocoar_auth_next_acme`
3. **In Marten-Tenancy registrieren**:
   `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`
4. **Marten-Schema applyen** (Tabellen, Indizes, Functions)
5. **OAuthRealmSeeder.SeedAsync** seedet die neue DB:
   - 5 Default-Scopes (`openid`, `email`, `profile`, `roles`,
     `offline_access`)
   - Internal-Login-Provider
6. **AuthorizationSeeder** seedet 3 Default-Rollen (System Admin, User
   Manager, Viewer)
7. **Realm-Document in `IGlobalStore`** (Master-DB, Schema `global`)
8. **RealmCache.Invalidate()** — nächster Request lädt neu

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

## Realm bearbeiten

```http
PATCH /api/admin/realms/{slug}
Content-Type: application/json

{
  "displayName": "Acme Corporation",
  "description": "Updated",
  "domains": ["acme.example.com", "auth.acme.com"]
}
```

`Slug` ist immutable. `CanManageTenants` ist nicht über PATCH
änderbar — würde eine Authorization-Eskalation ermöglichen.

## Realm deaktivieren

```http
PATCH /api/admin/realms/{slug}
{ "isActive": false }
```

`RealmCache` filtert auf `IsActive = true` — alle Requests an die
Realm-Domain landen bei `404`. Daten bleiben erhalten.

::: danger System-Realm
Der System-Realm darf nicht deaktiviert werden — der Endpoint blockt
das.
:::

## Realm hard-löschen

::: warning Nicht implementiert
Aktueller Stand: nur Soft-Delete (Deaktivierung). Die Tenant-DB wird
nicht gedroppt. Das ist ein offenes Roadmap-Item — Wolverine
durability-Agent muss sauber heruntergefahren, Tenant aus
`mt_tenant_databases` entfernt, alle Sessions invalidiert und am Ende
die DB gedroppt werden.
:::

## Setup-Flow für neuen Realm

Nach `POST /api/admin/realms`:

1. Browser auf der neuen Realm-Domain öffnen (z.B. `https://acme.example.com/`)
2. Frontend macht `GET /api/setup/status` → `{ needsSetup: true }`
3. Frontend redirected zu `/setup`
4. User legt First-Time-Admin an
5. Auto-Login als System-Admin im neuen Realm

Der erste User kommt automatisch in die "System-Admin"-Default-Gruppe,
die `app:admin` hat.

## Realm-Datenmodell

Das `Realm`-Dokument
(`src/dotnet/Cocoar.Auth.Domain/Realms/Realm.cs`):

```csharp
public class Realm
{
    public Guid Id { get; set; }
    public string Slug { get; set; }              // = TenantId, immutable
    public string DisplayName { get; set; }
    public string? Description { get; set; }
    public string[] Domains { get; set; }         // Host-Header-Matches
    public bool CanManageTenants { get; set; }    // darf Realm-CRUD
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

Lebt im `IGlobalStore` (Master-DB, Schema `global`) — nicht im
Tenant-Store, sonst Henne-Ei-Problem.
