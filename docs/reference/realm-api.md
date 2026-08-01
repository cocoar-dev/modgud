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
| `POST` | `/api/admin/realms/{slug}/admin-invites` | `realm:write` |
| `POST` | `/api/admin/realms/import` | `realm:write` (create a realm from a manifest) |
| `POST` | `/api/admin/realms/{slug}/apply` | `realm:write` (merge a manifest; `?prune=true` = full sync) |
| `GET` | `/api/admin/realms/{slug}/export` | `realm:read` (structure-only manifest) |
| `GET` | `/api/admin/realms/manifest-schema` | `realm:write` (JSON Schema of the manifest + example) |

See [Declarative Realm Provisioning](../admin/realm-provisioning) for the manifest
contract, merge-vs-prune semantics, and how to fetch the schema.

### Per-realm self-service (data plane — not control-plane)

A realm's own admin can manage **just that realm** from a manifest, without control-plane
powers. These run on the realm's **own host** and require **`realm:admin` in that realm**
(not the `control-plane` app); they cannot create or delete realms or target another realm.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/realm-config/manifest-schema` | `realm:admin` (in the realm) |
| `GET` | `/api/admin/realm-config/export` | `realm:admin` (in the realm) |
| `POST` | `/api/admin/realm-config/apply` | `realm:admin` (in the realm; `?prune=true` = full sync within the realm) |

::: tip Permission context
These permissions live in the **`control-plane`** App's catalog
(seeded only on the Control-Plane realm). The same string `realm:read`
in the `modgud` App's catalog would be a different permission. The
`realm:admin` realm-wide bypass grants all of them; see
[Permissions & gating](../concepts/permissions).
:::

## Create a realm

`POST` creates the realm independently from its administrators.
`InitialAdmin` remains an optional API convenience for callers that want
to create the realm and issue an invitation in one request; the admin UI
uses the separate invitation endpoint.
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
  "Domains": ["acme.example.com"]
}
```

### What happens

1. **Slug validation**: regex `^[a-z][a-z0-9-]{1,61}[a-z0-9]$`, no
   reserved word (`system`, `health`, `swagger`, `api`, `connect`, …)
2. **Create PostgreSQL DB** (raw SQL):
   `CREATE DATABASE <master-db>_acme`
3. **Register in Marten tenancy**:
   `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`
4. **Apply Marten schema** (tables, indexes, functions)
5. **`OAuthRealmSeeder.SeedAsync`** seeds the 6 default scopes
   (`openid`, `email`, `profile`, `roles`, `offline_access`,
   `permissions`) and the built-in Internal login provider.
6. **`AppRealmSeeder.SeedAsync`**: the `modgud` App is registered in
   the new tenant DB. The `control-plane` App is **only** seeded for
   the system realm — tenant realms physically cannot grant
   `realm:read`/`realm:write` (the App that owns those catalog
   entries doesn't exist in their tenant DB).
7. **Realm document** persisted in `IGlobalStore` (master DB, schema
   `global`).
8. **`RealmCache.Invalidate()`** — the next request loads it fresh.
9. When optional `InitialAdmin` is present, its invitation is issued
    atomically and returned as `InitialAdminInvite`; otherwise that
    response property is `null`/omitted.

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
    "CreatedAt": "2026-05-05T10:00:00Z"
  }
}
```

`IsControlPlane` is read-only — it appears in responses but is never
accepted in requests.

## Invite a realm admin

```http
POST /api/admin/realms/acme/admin-invites HTTP/1.1
Host: auth.example.com
Content-Type: application/json

{
  "UserName": "max",
  "Email": "max@acme.com",
  "Firstname": "Max",
  "Lastname": "Mustermann"
}
```

Issues a new single-use, 24-hour invitation. Every prior open admin
invitation in the realm is revoked, regardless of recipient, so at most
one link is active. The response contains `InitialAdminInviteDto`,
including the one-time `MagicLinkUrl` for SMTP-less development.
The recipient consumes the token at `POST /api/account/bootstrap-admin`
on the realm's host (see [Auth API](./auth-api)).

## Edit a realm

```http
PATCH /api/admin/realms/acme HTTP/1.1
Content-Type: application/json

{
  "DisplayName": "Acme Corporation",
  "Description": "Updated",
  "Domains": ["acme.example.com", "auth.acme.com"],
  "PrimaryDomain": "auth.acme.com",
  "IsActive": true
}
```

`Slug` is immutable. The patchable fields are exactly the five shown
above — `IsControlPlane` is not accepted in PATCH either. `PrimaryDomain`
must be one of the resulting `Domains` set; it is the realm's canonical
public host and doubles as the WebAuthn relying-party ID, so changing it
invalidates every passkey already registered in the realm (a passkey is
cryptographically bound to the RP ID it was created against) — treat it
as a rare, disruptive change.

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

```http
DELETE /api/admin/realms/acme?hard=true HTTP/1.1
Host: auth.example.com   # must be the control-plane host
```

Unlike the default `DELETE` (soft-delete = deactivation, data preserved),
`?hard=true` drops the tenant's PostgreSQL database
(`DROP DATABASE ... WITH (FORCE)`) and removes the global `Realm` record.
This is irreversible — there is no recovery path once it completes.

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
    public Dictionary<string, Guid> ApplicationDomains { get; set; }  // host → App.Id (ADR-0011)
    public string PrimaryDomain { get; set; }     // canonical public host + WebAuthn RP ID
    public bool IsControlPlane { get; set; }      // stored, transferable; exactly one holder
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

Lives in `IGlobalStore` (master DB, schema `global`) — not in the
tenant store, because that would create a chicken-and-egg problem.
