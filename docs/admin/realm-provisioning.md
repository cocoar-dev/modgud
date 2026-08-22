# Declarative Realm Provisioning

Provision a **complete realm from a single JSON document** — apps, OAuth
APIs/scopes/clients, roles, users, groups and realm settings — in one call,
at runtime. Think *realm-as-code*: instead of clicking (or scripting dozens of)
admin API calls, you `POST` a **manifest** and Modgud materialises the whole
realm by running the same operations the admin UI uses.

It's built for three jobs:

- **Bootstrap a realm reproducibly** — keep a realm's shape in version control and
  re-apply it.
- **Per-test realms** — an app's integration suite spins up a fresh, isolated realm
  per run (every realm is a physically separate database, so tests run in parallel),
  then tears it down.
- **Agents / automation** — a machine can fetch the [contract schema](#discover-the-schema)
  and author a valid manifest without reading any source.

## Two surfaces — pick by who's calling

The same manifest format is applied through **two** surfaces, differing in scope and
who's allowed:

| | Control-plane provisioning | Per-realm self-service |
|---|---|---|
| **For** | Operators managing the deployment | A realm's own admin (delegate this) |
| **Path** | `/api/admin/realms/*` | `/api/admin/realm-config/*` |
| **Runs on** | Control-Plane realm only (404 elsewhere) | The realm's own host (any realm) |
| **Permission** | `realm:write` on the `control-plane` app | `realm:admin` **in that realm** |
| **Can** | Create / update / export / **delete any** realm | Update + export **its own** realm (incl. prune) |
| **Cannot** | — | Create or delete realms; touch another realm |

If you run a **shared** Modgud and want to hand one realm to an app team (or an
agent) so they manage *only* that realm without operator powers, use
[per-realm self-service](#per-realm-self-service). For full lifecycle control
(creating/removing realms), use the control-plane surface below.

## Control-plane endpoints

All under `/api/admin/realms`, all requiring **`realm:write`** on the
`control-plane` app (the `realm:admin` bypass also grants it), and only on the
Control-Plane host ([404 elsewhere](../concepts/control-plane)):

| Method | Path | What it does |
|---|---|---|
| `POST` | `/import` | Create a **new** realm from a manifest. The slug must not exist. All-or-nothing: a failed import rolls the whole realm back. |
| `POST` | `/{slug}/apply` | **Merge** a manifest into an existing realm (upsert per entity). Never drops the database. |
| `POST` | `/{slug}/apply?prune=true` | **Full sync** — like apply, then delete entities present in the realm but absent from the manifest. |
| `GET` | `/{slug}/export` | Export the realm as a manifest (structure-only — never secrets or password hashes). |
| `GET` | `/manifest-schema` | The JSON Schema for the manifest (see [below](#discover-the-schema)). |
| `DELETE` | `/{slug}?hard=true` | **Hard-delete** — drop the tenant database. Without `?hard=true` it's the reversible soft-delete. |

Authenticate as a Control-Plane admin (cookie or bearer) before calling these —
e.g. `POST /api/account/login` for a cookie session.

## Discover the schema

You don't have to guess property names. The full, machine-readable **JSON Schema**
of the manifest — every field, its type, what's required, a description per field,
and a worked example — is served live:

```http
GET /api/admin/realms/manifest-schema      (realm:write)
```

The schema is **generated from the live manifest type** using the API's own JSON
settings, so it can never drift from what `import`/`apply` actually accept. It's
gated with the same permission as import/apply: only a caller who could apply a
manifest may fetch its schema.

```bash
curl -b cookies.txt https://<control-plane-host>/api/admin/realms/manifest-schema
```

Point any JSON-Schema-aware tool (or an agent) at the result and it can validate
and author manifests directly.

## The manifest at a glance

A manifest is one object with a required `Realm` plus optional entity lists.
**Cross-references use stable keys, never server-generated ids:**

- APIs / scopes / clients / roles reference an app by its **`Slug`**.
- Permissions are addressed as **`resource:action`** (e.g. `invoice:read`).
- Groups list **`Members`** (user keys) and **`Roles`** (role keys). Group
  membership is the *only* way users get roles.

```jsonc
{
  "Realm": {                               // REQUIRED — shell + first admin
    "Slug": "acme",
    "DisplayName": "Acme",
    "Domains": ["acme.example.com"],
    "InitialAdmin": { "UserName": "admin", "Email": "admin@acme.example.com" }
  },
  "Settings": { /* optional realm-settings patch (self-reg, native grants, …) */ },
  "Apps":    [ { "Slug": "acme", "DisplayName": "Acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ] } ],
  "Apis":    [ { "Name": "acme-api", "App": "acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ] } ],
  "Scopes":  [ { "Name": "invoice.read", "App": "acme", "Resources": ["acme-api"] } ],
  "Clients": [ { "ClientId": "acme-web", "ClientType": "confidential",
                 "RedirectUris": ["https://acme.example.com/cb"],
                 "Scopes": ["openid", "invoice.read"],
                 "AllowedGrantTypes": ["authorization_code", "refresh_token"],
                 "Apps": ["acme"] } ],
  "Roles":   [ { "Key": "acme-admin", "Name": "acme-admin", "App": "acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ] } ],
  "Users":   [ { "Key": "alice", "Email": "alice@acme.example.com", "UserName": "alice" } ],
  "Groups":  [ { "Name": "Admins", "Members": ["alice"], "Roles": ["acme-admin"] } ]
}
```

See the [schema](#discover-the-schema) for every field and its meaning.

## Quickstart

```bash
AUTH=https://<control-plane-host>

# 1) Log in as a Control-Plane admin (cookie)
curl -c cookies.txt -X POST "$AUTH/api/account/login" \
  -H 'Content-Type: application/json' \
  -d '{"UserName":"admin","Password":"<password>"}'

# 2) Create the realm from a manifest → 201, with any generated client secrets
curl -b cookies.txt -X POST "$AUTH/api/admin/realms/import" \
  -H 'Content-Type: application/json' -d @manifest.json
# → {"Slug":"acme","PrimaryDomain":"acme.example.com","ClientSecrets":{"acme-web":"…"}}

# 3) Later: re-apply changes in place (merge)
curl -b cookies.txt -X POST "$AUTH/api/admin/realms/acme/apply" \
  -H 'Content-Type: application/json' -d @manifest.json

# 4) Tear it down
curl -b cookies.txt -X DELETE "$AUTH/api/admin/realms/acme?hard=true"
```

::: tip Client secrets
Confidential clients get a **generated secret returned only at import** (in
`ClientSecrets`). Store it then — there's no way to read it back later. Existing
clients keep their secret across `apply`.
:::

## Apply: merge vs. prune

`apply` is a desired-state merge **for the fields the manifest carries**:

- Boolean flags are nullable — omit one to leave it unchanged (it takes the shipped
  default only on create).
- Scalar strings and non-empty lists replace; an omitted/empty value is left
  unchanged (apply never clears a list or detaches a link).
- App-catalog permission ids are preserved across updates, so unchanged permissions
  keep their grants.

Add **`?prune=true`** to make it a full sync: after the merge, entities in the realm
that are *absent* from the manifest are deleted (in dependency order). To prevent a
manifest from locking a realm out, prune **never deletes** the system app, auto-seeded
standard scopes, service-account-linked clients, or anything conferring `realm:admin`
(a realm-admin role, any current admin user, or an admin-conferring group).

## Export

`GET /{slug}/export` returns the realm as a manifest — the inverse of import. It is
**structure-only**: it never emits client secrets or password hashes (those are
one-way), and it omits auto-seeded standard scopes / system apps / SA-linked clients.
This is deliberate — it is *not* a backup (a real backup needs the whole tenant
database). Its purpose is **get-config → edit → re-apply**: export a realm, add a user
password or tweak a setting, and `POST` it back to `/{slug}/apply`. Because confidential
clients regenerate a secret on import and users can be created passwordless, a
structure-only manifest still re-applies into a fully working realm.

## Per-realm self-service

On a **shared** deployment you often want to delegate one realm to its owner — an app
team or an agent — so they can fully manage *that* realm's config and entities, but
**not** create or delete realms and **not** see any other realm. That is exactly what a
**`realm:admin` in that realm** can do, through `/api/admin/realm-config/*`:

| Method | Path | What it does |
|---|---|---|
| `GET`  | `/api/admin/realm-config/manifest-schema` | The manifest JSON Schema (identical to the control-plane one). |
| `GET`  | `/api/admin/realm-config/export` | Export **this** realm as a manifest. |
| `POST` | `/api/admin/realm-config/apply` | Apply a manifest to **this** realm (merge; `?prune=true` = full sync within the realm). |

- **Scope is the calling realm** — resolved from the request host, never from a slug in
  the body. A manifest whose `Realm.Slug` names a *different* realm is rejected
  (`Manifest.SlugMismatch`). There is no `import` and no realm-delete here — realm
  lifecycle stays control-plane-only.
- **Permission**: `realm:admin` in the realm being called. Nothing control-plane.
- **Same engine, same protections** as the control-plane path: prune is bounded to the
  realm and never removes the system app, standard scopes, service-account clients, or any
  `realm:admin` path — so a manifest can't lock the realm out.

### Delegating a realm

To grant someone management of exactly one realm:

1. **Create the realm** (control-plane: `import`, or the admin UI).
2. **In that realm, give the principal `realm:admin`** — either a **user** (interactive)
   or a **service account** (machine / agent, `client_credentials`). Both work. For a
   bearer caller, Modgud evaluates `realm:admin` live from the principal's current groups
   and roles; the permission is not copied into the token.
3. For machine access, allow the protected `modgud.management` scope on the linked
   Service Account credential and request a token for resource
   `urn:modgud:management-api`.
4. Call `/api/admin/realm-config/*` against the realm's host with that cookie or bearer.

That credential can do everything to *its* realm's config and **nothing** to any other
realm — and cannot create or delete realms.

```bash
REALM=https://acme.example.com   # the realm's own host

curl -c cookies.txt -X POST "$REALM/api/account/login" \
  -H 'Content-Type: application/json' -d '{"UserName":"realm-admin","Password":"<password>"}'

curl -b cookies.txt "$REALM/api/admin/realm-config/export"             # current config
curl -b cookies.txt -X POST "$REALM/api/admin/realm-config/apply" \
  -H 'Content-Type: application/json' -d @manifest.json                # apply edits (+ ?prune=true)
```

For unattended provisioning, use the fixed [Management API contract](../integrate/management-api)
instead of an admin cookie:

```bash
TOKEN=$(curl -sS -X POST "$REALM/connect/token" \
  -d 'grant_type=client_credentials' \
  -d 'client_id=<linked-service-account-client>' \
  -d 'client_secret=<secret>' \
  -d 'scope=modgud.management' \
  -d 'resource=urn:modgud:management-api' | jq -r '.access_token')

curl -H "Authorization: Bearer $TOKEN" \
  "$REALM/api/admin/realm-config/export"
curl -X POST -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d @manifest.json \
  "$REALM/api/admin/realm-config/apply"
```

## Provisioning from a .NET test suite

For .NET apps, the **`Modgud.Provisioning.TestKit`** package wraps these endpoints
with automatic teardown — give each test a unique slug and dispose to hard-delete:

```csharp
var http = new HttpClient(new HttpClientHandler { CookieContainer = new() })
    { BaseAddress = new Uri("https://<control-plane-host>") };
await http.PostAsJsonAsync("/api/account/login",
    new { UserName = "admin", Password = "<password>" });

var kit = new ModgudProvisioningClient(http);
await using var realm = await kit.ImportRealmAsync(manifest);   // dispose → hard-delete
var secret = realm.SecretFor("acme-web");
```

## Caveat — using a provisioned realm for OAuth flows

Creating, updating and deleting realms uses the current Control-Plane host. But
**driving OAuth flows _against_ a provisioned realm is
host-routed**: Modgud resolves the tenant from the request's `Host` header
(`Realm.Domains`), and each realm's issuer is `https://{PrimaryDomain}`. So a token
request for a realm must arrive with that realm's host. For machine flows
(`client_credentials`, native grants, introspection) that's just a `Host` header; for
browser authorization-code flows the realm host must be reachable and match the issuer
you configure in the client.
