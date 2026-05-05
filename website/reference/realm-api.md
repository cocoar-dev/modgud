# Realm Endpoints

Realm management is only callable from the **Control-Plane realm** (the realm
flagged `IsControlPlane = true`). On any other host the endpoints return
**404** — not 403, because the existence of the realm-management surface
must not be leaked to tenant realms. See [Concepts: Control Plane](../concepts/control-plane)
for the full three-layer defence.

Endpoints in `Cocoar.Auth.Api/Features/Admin/RealmsEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/realms` | `control-plane:realm:read` |
| `GET` | `/api/admin/realms/{slug}` | `control-plane:realm:read` |
| `POST` | `/api/admin/realms` | `control-plane:realm:write` |
| `PATCH` | `/api/admin/realms/{slug}` | `control-plane:realm:write` |
| `DELETE` | `/api/admin/realms/{slug}` | `control-plane:realm:write` (soft-delete = deactivate) |
| `POST` | `/api/admin/realms/{slug}/resend-bootstrap-invite` | `control-plane:realm:write` |

## Create a realm

`POST` requires an `InitialAdmin` payload. A realm without a recipient on file
would have no admin path; the endpoint refuses to create one.

```http
POST /api/admin/realms HTTP/1.1
Host: auth.example.com
Content-Type: application/json

{
  "Slug": "acme",
  "DisplayName": "Acme Corp",
  "Description": "Acme Corporation Identity",
  "Domains": ["acme.example.com"],
  "IsControlPlane": false,
  "InitialAdmin": {
    "UserName": "max",
    "Email": "max@acme.com",
    "Firstname": "Max",
    "Lastname": "Mustermann"
  }
}
```

### What happens

1. **Slug validation**: regex `^[a-z][a-z0-9-]{1,61}[a-z0-9]$`, no
   reserved word (`system`, `health`, `swagger`, `api`, `connect`, ...)
2. **`IsControlPlane` invariant**: a new realm flagged Control-Plane is
   only accepted if no other active CP realm exists (`Realm.ControlPlaneAlreadyExists`
   otherwise — exactly one CP per deployment).
3. **`InitialAdmin` validation**: `UserName` and `Email` are required;
   `Firstname` and `Lastname` are optional.
4. **Create PostgreSQL DB** (raw SQL): `CREATE DATABASE <master-db>_acme`
5. **Register in Marten tenancy**: `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`
6. **Apply Marten schema** (tables, indexes, functions)
7. **`OAuthRealmSeeder.SeedAsync`** seeds:
   - 5 default scopes (`openid`, `email`, `profile`, `roles`, `offline_access`)
   - Internal login provider
8. **`AppRealmSeeder.SeedAsync`**: the `cocoar-auth` app is registered in
   the new tenant DB. The `control-plane` app is **only** seeded for realms
   where `IsControlPlane = true` — tenant realms cannot grant
   `control-plane:realm:*` permissions.
9. **Realm document** persisted in `IGlobalStore` (master DB, schema `global`).
10. **`RealmCache.Invalidate()`** — the next request loads it fresh.
11. **Bootstrap-invite issued** atomically into the new tenant DB. The recipient's
    SHA-256-hashed token is stored as `PendingAdminInvite`; the plaintext is
    embedded in the magic-link URL emailed to `InitialAdmin.Email`.

### Response (201 Created)

```json
{
  "Realm": {
    "Id": "0c12...",
    "Slug": "acme",
    "DisplayName": "Acme Corp",
    "Description": "Acme Corporation Identity",
    "Domains": ["acme.example.com"],
    "IsControlPlane": false,
    "IsActive": true,
    "NeedsSetup": false,
    "CreatedAt": "2026-05-05T10:00:00Z"
  },
  "InitialAdminInvite": {
    "UserName": "max",
    "Email": "max@acme.com",
    "ExpiresAt": "2026-05-12T10:00:00Z",
    "MagicLinkUrl": "https://acme.example.com/bootstrap?token=…"
  }
}
```

`MagicLinkUrl` is returned **only here**, only this once — capture it if SMTP
delivery isn't reliable in the issuing environment. The plaintext token is
not retrievable from the API later. To re-issue use the resend endpoint.

The recipient consumes the token at `POST /api/account/bootstrap-admin` on
the new realm's host (see [Auth API](./auth-api)).

## Resend a bootstrap-invite

```http
POST /api/admin/realms/acme/resend-bootstrap-invite HTTP/1.1
Host: auth.example.com
```

Re-uses the recipient identity (UserName + Email + Firstname + Lastname) from
the **most recent prior invite** — no body needed. The previous invite is
revoked (`UsedAt` set), a fresh 7-day token is issued, the email is sent
again, and the new `MagicLinkUrl` is returned in the response (same shape as
`InitialAdminInvite` above).

Returns `404 Realm.NoPriorInvite` if no invite was ever issued (e.g. a realm
whose first admin was created via the recovery CLI in direct mode).

## Edit a realm

```http
PATCH /api/admin/realms/acme HTTP/1.1
Content-Type: application/json

{
  "DisplayName": "Acme Corporation",
  "Description": "Updated",
  "Domains": ["acme.example.com", "auth.acme.com"],
  "IsControlPlane": false,
  "IsActive": true
}
```

`Slug` is immutable.

`IsControlPlane` can be toggled, but two invariants are enforced:

- **Cannot remove the flag from the last Control-Plane realm.** Returns
  `400 Realm.CannotRemoveControlPlaneFlag`. Promote another realm to CP
  first.
- **Cannot promote a second realm.** Returns `400 Realm.ControlPlaneAlreadyExists`.
  Demote the existing CP first. The hand-off is a deliberate two-step.

## Deactivate a realm

```http
PATCH /api/admin/realms/acme
{ "IsActive": false }
```

`RealmCache` filters on `IsActive = true` — all requests to the realm
domain land at 404. Data is preserved.

The same invariant blocks deactivating the last Control-Plane realm
(`Realm.CannotDeactivateControlPlane`).

## Hard-delete a realm

::: warning Not implemented
Current state: only soft-delete (deactivation). The tenant DB is not
dropped. Roadmap: the Wolverine durability agent has to be shut down
cleanly, the tenant removed from `mt_tenant_databases`, all sessions
invalidated, and finally the DB dropped.
:::

## Realm data model

`src/dotnet/Cocoar.Auth.Domain/Realms/Realm.cs`:

```csharp
public class Realm
{
    public Guid Id { get; set; }
    public string Slug { get; set; }              // = TenantId, immutable
    public string DisplayName { get; set; }
    public string? Description { get; set; }
    public string[] Domains { get; set; }         // Host-header matches
    public bool IsControlPlane { get; set; }      // exactly one per deployment
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

Lives in `IGlobalStore` (master DB, schema `global`) — not in the tenant
store, because that would create a chicken-and-egg problem.
