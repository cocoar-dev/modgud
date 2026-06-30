# Realm Endpoints

Realm management is only callable from the **Control-Plane realm** (the
realm flagged `IsControlPlane = true`, which is the system realm).
On any other host the endpoints return **404** — not 403, because the
existence of the realm-management surface must not be leaked to tenant
realms. See [Concepts: Control Plane](../concepts/control-plane) for
the full three-layer defence.

Endpoints in `Modgud.Api/Features/Admin/RealmsEndpoints.cs`.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/realms` | `realm:read` |
| `GET` | `/api/admin/realms/{slug}` | `realm:read` |
| `POST` | `/api/admin/realms` | `realm:write` |
| `PATCH` | `/api/admin/realms/{slug}` | `realm:write` |
| `DELETE` | `/api/admin/realms/{slug}` | `realm:write` (soft-delete = deactivate; `?hard=true` drops the tenant database) |
| `POST` | `/api/admin/realms/{slug}/resend-bootstrap-invite` | `realm:write` |
| `POST` | `/api/admin/realms/import` | `realm:write` (create a realm from a manifest) |
| `POST` | `/api/admin/realms/{slug}/apply` | `realm:write` (merge a manifest; `?prune=true` = full sync) |
| `GET` | `/api/admin/realms/{slug}/export` | `realm:read` (structure-only manifest) |
| `GET` | `/api/admin/realms/manifest-schema` | `realm:write` (JSON Schema of the manifest + example) |

See [Declarative Realm Provisioning](../admin/realm-provisioning) for the manifest
contract, merge-vs-prune semantics, and how to fetch the schema.

::: tip Permission context
These permissions live in the **`control-plane`** App's catalog
(seeded only on the Control-Plane realm). The same string `realm:read`
in the `modgud` App's catalog would be a different permission. The
`realm:admin` realm-wide bypass grants all of them; see
[Permissions & gating](../concepts/permissions).
:::

## Create a realm

`POST` requires an `InitialAdmin` payload. A realm without a recipient
on file would have no admin path; the endpoint refuses to create one.
The Control-Plane flag is a stored, transferable field; you cannot set it on
create (new realms are never the control plane). See
[Transfer the control plane](#transfer-the-control-plane).

```http
POST /api/admin/realms HTTP/1.1
Host: auth.example.com
Content-Type: application/json

{
  "Slug": "acme",
  "DisplayName": "Acme Corp",
  "Description": "Acme Corporation Identity",
  "Domains": ["acme.example.com"],
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
   reserved word (`system`, `health`, `swagger`, `api`, `connect`, …)
2. **`InitialAdmin` validation**: `UserName` and `Email` are required;
   `Firstname` and `Lastname` are optional.
3. **Create PostgreSQL DB** (raw SQL):
   `CREATE DATABASE <master-db>_acme`
4. **Register in Marten tenancy**:
   `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`
5. **Apply Marten schema** (tables, indexes, functions)
6. **`OAuthRealmSeeder.SeedAsync`** seeds the 6 default scopes
   (`openid`, `email`, `profile`, `roles`, `offline_access`,
   `permissions`) and the built-in Internal login provider.
7. **`AppRealmSeeder.SeedAsync`**: the `modgud` App is registered in
   the new tenant DB. The `control-plane` App is **only** seeded for
   the system realm — tenant realms physically cannot grant
   `realm:read`/`realm:write` (the App that owns those catalog
   entries doesn't exist in their tenant DB).
8. **Realm document** persisted in `IGlobalStore` (master DB, schema
   `global`).
9. **`RealmCache.Invalidate()`** — the next request loads it fresh.
10. **Bootstrap-invite issued** atomically into the new tenant DB.
    The recipient's SHA-256-hashed token is stored as
    `PendingAdminInvite`; the plaintext is embedded in the magic-link
    URL emailed to `InitialAdmin.Email`.

### Response (201 Created)

```json
{
  "Realm": {
    "Id": "0c12…",
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

`IsControlPlane` is read-only — it appears in responses but is never
accepted in requests. `MagicLinkUrl` is returned **only here**, only
this once — capture it if SMTP delivery isn't reliable in the issuing
environment. To re-issue use the resend endpoint.

The recipient consumes the token at `POST /api/account/bootstrap-admin`
on the new realm's host (see [Auth API](./auth-api)).

## Resend a bootstrap-invite

```http
POST /api/admin/realms/acme/resend-bootstrap-invite HTTP/1.1
Host: auth.example.com
```

Re-uses the recipient identity (UserName + Email + Firstname +
Lastname) from the **most recent prior invite** — no body needed. The
previous invite is revoked (`UsedAt` set), a fresh 7-day token is
issued, the email is sent again, and the new `MagicLinkUrl` is
returned in the response (same shape as `InitialAdminInvite` above).

Returns `404 Realm.NoPriorInvite` if no invite was ever issued (e.g.
a realm whose first admin was created via the recovery CLI in direct
mode).

## Edit a realm

```http
PATCH /api/admin/realms/acme HTTP/1.1
Content-Type: application/json

{
  "DisplayName": "Acme Corporation",
  "Description": "Updated",
  "Domains": ["acme.example.com", "auth.acme.com"],
  "IsActive": true
}
```

`Slug` is immutable. The patchable fields are exactly the four shown
above — `IsControlPlane` is not accepted in PATCH either.

## Deactivate a realm

```http
PATCH /api/admin/realms/acme
{ "IsActive": false }
```

`RealmCache` filters on `IsActive = true` — all requests to the realm
domain land at 404. Data is preserved.

The Control-Plane realm cannot be deactivated
(`Realm.CannotDeactivateControlPlane`).

## Transfer the control plane

```http
POST /api/admin/realms/{slug}/transfer-control-plane HTTP/1.1
Host: auth.example.com   # must be the current control-plane host
```

Moves the stored `IsControlPlane` flag to `{slug}` (the realm that should
*become* the control plane) and clears it on every other holder, in one
transaction. Gated by `control-plane:realm:write` **and** the control-plane
routing gate (the caller's host must currently be the control plane). The
target must exist and be active.

| Response | Meaning |
|---|---|
| `200 OK` (the updated realm) | Flag moved (or already the sole holder — idempotent). |
| `404 Not Found` | Target doesn't exist, **or** the caller's host isn't the control plane (the gate hides the surface). |
| `400` `Realm.TargetInactive` | Target realm is deactivated. |

After the move the calling host stops being the control plane — subsequent
`/api/admin/realms` requests there return `404`. The target realm's existing
`realm:admin` users gain cross-realm administration (authority is `realm:admin`
within the flag-holding realm; no permission migration). See
[Control Plane](../concepts/control-plane#transferring-the-control-plane).

## Hard-delete a realm

::: warning Not implemented
Current state: only soft-delete (deactivation). The tenant DB is not
dropped. Roadmap: the Wolverine durability agent has to be shut down
cleanly, the tenant removed from `mt_tenant_databases`, all sessions
invalidated, and finally the DB dropped.
:::

## Realm data model

`src/dotnet/Modgud.Domain/Realms/Realm.cs`:

```csharp
public class Realm
{
    public Guid Id { get; set; }
    public string Slug { get; set; }              // = TenantId, immutable
    public string DisplayName { get; set; }
    public string? Description { get; set; }
    public string[] Domains { get; set; }         // Host-header matches
    public bool IsControlPlane { get; set; }      // stored, transferable; exactly one holder
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

Lives in `IGlobalStore` (master DB, schema `global`) — not in the
tenant store, because that would create a chicken-and-egg problem.
