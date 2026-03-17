# Realm Endpoints

Realm management is only available from the **system realm**. The `[SystemRealmOnly]` filter returns 404 for requests from tenant realms.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/admin/realms` | List all realms |
| `GET` | `/api/admin/realms/{slug}` | Get realm by slug |
| `POST` | `/api/admin/realms` | Create new realm |
| `PATCH` | `/api/admin/realms/{slug}` | Update realm |
| `DELETE` | `/api/admin/realms/{slug}` | Delete realm |

## Create Realm

```json
POST /api/admin/realms
{
  "slug": "acme",
  "displayName": "Acme Corp",
  "description": "Acme Corporation identity realm"
}
```

### What Happens

1. A new PostgreSQL database `cocoar_auth_acme` is created
2. The tenant is registered in Marten's master table
3. Marten schema (tables, indexes, functions) is applied
4. Default OpenIddict scopes are seeded (`openid`, `email`, `profile`, `roles`, `offline_access`)
5. Built-in login providers are seeded
6. Realm metadata is stored in the system tenant

### Response

```json
{
  "id": "...",
  "slug": "acme",
  "displayName": "Acme Corp",
  "description": "Acme Corporation identity realm",
  "isActive": true,
  "isSystem": false,
  "needsSetup": true,
  "createdAt": "2026-03-16T15:00:00Z"
}
```

## Update Realm

```json
PATCH /api/admin/realms/acme
{
  "displayName": "Acme Corporation",
  "isActive": false
}
```

::: warning
The system realm cannot be deactivated or deleted.
:::

## Realm Setup Flow

After creating a realm, the first visitor to `/realms/{slug}/` triggers the setup flow:

1. Frontend detects `needsSetup: true` from `GET /realms/{slug}/api/setup/status`
2. Redirects to `/realms/{slug}/setup`
3. User creates the realm's first admin account
4. Auto-login completes the setup
