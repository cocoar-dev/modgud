# Realms

## What is a realm?

A realm is a **fully autonomous identity provider**. It is the
fundamental isolation boundary in modgud.

Per realm:

- its own **PostgreSQL database** (`<master-db>_<slug>`)
- its own **users and groups**
- its own **roles and permissions**
- its own **OAuth clients, scopes, APIs**
- its own **OIDC discovery endpoint**
- its own **login providers** (Internal + OIDC IdPs)
- its own **cookie domain**

Each realm looks like a standalone modgud installation —
because that is essentially what it is.

::: tip Realm (tenant) vs. Application
The realm is the **hard** boundary above. An **Application** is a **soft
facet** *within* a realm — it can carry its own subdomain, branding and a
per-app override of self-registration / native-grant / DCR / CIMD policy,
but it shares the realm's user pool, signing keys and OIDC issuer (a
request on an App subdomain still mints tokens under the realm's canonical
issuer). Promote an App to its own realm only when you need independent key
rotation, breach containment, or data isolation. See
[Apps & resource access](./apps-and-resource-access#the-apps-second-facet-a-login-experience-adr-0011)
and [Admin → Applications](../admin/applications#application-settings).
:::

## Domain-based routing

Modgud identifies the realm via the **HTTP Host header** —
not via URL paths. Each realm has one or more configured domains.

```
acme.example.com         → Realm "acme"
auth.acme.example.com    → Realm "acme"  (second domain for the same realm)
finance.example.com      → Realm "finance"
system.example.com       → System realm
localhost                → System realm  (single-tenant fallback in dev)
```

`RealmMiddleware` (in `Modgud.Api.Middleware`) runs before all
other middlewares and:

1. Reads `request.Host.Host`
2. Looks up a match in `IRealmCache`
3. Sets `HttpContext.Items["TenantId"] = realm.Slug`
4. If no match → `404`

The cache is warmed at boot and invalidated on realm CUD.

::: tip Single-tenant fallback in dev
If only one realm is active AND the host is a localhost variant
(`localhost`, `127.0.0.1`, `::1`, `0.0.0.0`), the cache returns that
single realm — even if it does not have the localhost domain in its
list. This makes a single-realm dev boot work without a hosts-file
entry.
:::

## Database-per-tenant via Marten

Modgud uses Marten's `MasterTableTenancy`:

```mermaid
graph TD
    Master[(<master-db><br/>= Master DB)]
    Master -->|realms.mt_tenant_databases| System[(<master-db>_system)]
    Master -->|realms.mt_tenant_databases| Acme[(<master-db>_acme)]
    Master -->|realms.mt_tenant_databases| Finance[(<master-db>_finance)]

    Master -->|global Schema| GlobalRealm["Realm-Documents<br/>(IGlobalStore)"]
    System -->|tenant data| SystemUsers[Users, Groups, OAuth, ...]
    Acme -->|tenant data| AcmeUsers[Users, Groups, OAuth, ...]
    Finance -->|tenant data| FinanceUsers[Users, Groups, OAuth, ...]
```

| Database | Contents |
|---|---|
| `<master-db>` (Master) | Schema `realms.mt_tenant_databases` (tenant registry) + schema `global` (Realm documents) |
| `<master-db>_system` | System realm data (users, groups, ...) — its own physical DB, like every other realm |
| `<master-db>_<slug>` | A separate physical DB per additional realm |

::: info Master DB vs. system realm
The master DB is pure control-plane infrastructure — the tenant registry
(`realms.mt_tenant_databases`) + the global Realm store (schema `global`) +
Wolverine durability. It is **not** a tenant. Every realm, including the
bootstrap `system` realm, lives in its own `<master-db>_<slug>` database.
That makes the system realm an equal, deletable peer so the
[control plane](./control-plane.md) can be transferred off it.
:::

### Tenant resolution in code

`TenantedSessionFactory` (Marten `ISessionFactory`) reads the `TenantId`
from `HttpContext.Items` and opens a tenant-scoped session:

```csharp
public IDocumentSession OpenSession()
    => _store.LightweightSession(ResolveTenantId());

private string ResolveTenantId()
    => _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
       ?? TenantConstants.SystemTenantId;
```

Every `IDocumentSession`/`IQuerySession` injection is therefore
automatically realm-scoped. Background services (without HttpContext)
fall back to the system tenant.

### GlobalStore for realm documents

The `Realm` document itself cannot live in the tenant store —
otherwise there would be a chicken-and-egg problem. It lives in a
separate Marten store (`IGlobalStore`) that writes to schema `global`
of the master DB.

`RealmCache` loads the realm list from there.

## Realm lifecycle

### 1. First-time bootstrap

On first start:

1. **Create the master DB and the `<master-db>_system` DB** (raw SQL, because
   Marten cannot `CREATE DATABASE` on an active connection)
2. **Apply the Marten schema** → `realms.mt_tenant_databases` is created
3. **Register the system tenant** → `tenancy.AddDatabaseRecordAsync("system", systemCs)` (pointing at `<master-db>_system`, not the master DB)
4. **Apply the Marten schema again** → per-tenant tables for the system realm, in its own DB
5. **Seed the system realm document** in `IGlobalStore`, stamped `IsControlPlane = true`
6. **Seed default OAuth scopes + the Internal login provider**
7. **Seed the `modgud` and `control-plane` apps** into the system tenant DB
8. **Warm `RealmCache`**
9. The instance is ready, but has zero users — the first admin is created via the [recovery CLI](../operate/recovery-cli) or, for additional realms, by an existing CP-admin via `POST /api/admin/realms`. See [First-time setup](../getting-started/first-time-setup).

### 2. Create additional realms

Only users with `control-plane:realm:write` **on the Control-Plane realm**
can do this. See [Control Plane](./control-plane.md) for the cross-realm
admin model — in short: realm CRUD lives on a dedicated app slug
(`control-plane`) that is only seeded into the Control-Plane realm's DB,
and the routing layer 404s the endpoint on tenant hosts.

```http
POST /api/admin/realms
{
  "Slug": "acme",
  "DisplayName": "Acme Corp",
  "Domains": ["acme.example.com"],
  "InitialAdmin": {
    "UserName": "max",
    "Email": "max@acme.com"
  }
}
```

Backend:

1. Validates `slug` (regex, no reserved word). New realms are never the
   control plane — the flag defaults to false and there is no create-time
   switch; the role only moves via [transfer](./control-plane.md#transferring-the-control-plane).
2. `CREATE DATABASE <master-db>_acme` (raw SQL).
3. `tenancy.AddDatabaseRecordAsync("acme", connStringForAcme)`.
4. `Storage.ApplyAllConfiguredChangesToDatabaseAsync()`.
5. **`OAuthRealmSeeder`** → 5 default scopes + Internal login provider.
6. **`AppRealmSeeder`** → registers the `modgud` app in the new tenant DB. The `control-plane` app is **not** seeded — it only exists in the Control-Plane realm.
7. Save the `Realm` document in `IGlobalStore`.
8. `RealmCache.Invalidate()`.
9. **Bootstrap-invite** issued atomically: writes a `PendingAdminInvite` into the new tenant DB, sends a magic-link email to `InitialAdmin.Email`, returns the URL in the API response.

The recipient clicks the magic-link, lands at `/bootstrap?token=…`, sets a password, and is auto-signed-in with `realm:admin`. The first user creation runs through `RealmAdminBootstrapper`, which atomically seeds the three default roles and adds the user to the `Administratoren` group.

### 3. Deactivate a realm

```http
PATCH /api/admin/realms/{slug}
{ "isActive": false }
```

`RealmCache` filters on `IsActive = true` — inactive realms are no
longer resolved, all requests to the domain land at `404`. The data
stays in the DB.

::: danger Do not deactivate the system realm
The system realm must not be deactivated — otherwise you have no way
back into the system.
:::

### 4. Hard-delete a realm

::: warning Work in progress
Currently only soft-delete (deactivation) is implemented. Hard-delete
would have to drop the tenant DB cleanly, shut down Wolverine's
durability agent, invalidate sessions — complex. Roadmap item.
:::

## OIDC endpoints per realm

Since each realm has its own domain, it also has its own OIDC
endpoints:

| Endpoint | Acme |
|---|---|
| Discovery | `https://acme.example.com/.well-known/openid-configuration` |
| Authorize | `https://acme.example.com/connect/authorize` |
| Token | `https://acme.example.com/connect/token` |
| UserInfo | `https://acme.example.com/connect/userinfo` |
| End Session | `https://acme.example.com/connect/logout` |
| Introspect | `https://acme.example.com/connect/introspect` |
| Revoke | `https://acme.example.com/connect/revoke` |

The `RealmIssuerHandler` (an OpenIddict pipeline hook) makes sure
the discovery document emits the correct issuer. Tokens from realm
A are not valid in realm B — the issuer mismatch is enough to reject
them.

## Cross-realm guarantees

| Guarantee | Mechanism |
|---|---|
| No user-data leaks | Database-per-tenant, physical DB boundary |
| No permission leaks | Per-tenant Marten sessions, no cross-tenant joins |
| No token leaks | Issuer-claim check + per-realm OpenIddict stores |
| No cookie leaks | Cookie domain per realm |
| No SignalR leaks | Hub connection is auth-gated, runs in the realm context |
