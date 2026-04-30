# Realms

A **Realm** in Cocoar.Auth is a tenant — a fully isolated namespace with its own database, users, groups, OAuth clients, and apps. Realms are how multi-tenant Cocoar.Auth deployments separate customers / environments / staging.

::: info When do I need multiple realms?
- **Multiple customers** sharing one Cocoar.Auth instance (each gets their own realm)
- **Stage separation** (production, staging, development) on shared infrastructure
- **Compliance isolation** (some customer data must not coexist in the same DB)

Single-tenant deployments only need the system realm — provisioned automatically on first start.
:::

![Realm list](/screenshots/admin-realms-liste.png)

## The system realm

The first realm, with slug `system`, is created automatically by `EnsureSystemRealmExistsAsync` on app startup. It has a special property: `CanManageTenants = true` — that's the realm where the tenant-management UI is exposed. Other realms can have this flag too if you want to delegate tenant management.

Domains: `system.localhost`, `localhost`, `127.0.0.1`. Routes hitting any of those resolve to the system realm.

## Realm fields

| Field | Meaning |
| --- | --- |
| Slug | URL-safe identifier, 3-63 chars, immutable. Determines the tenant DB name (`<main-db>_<slug>`). |
| Display Name | UI label |
| Description | Optional |
| Domains | Comma-separated list of hostnames that route to this realm |
| CanManageTenants | If true, this realm's admin can create/edit other realms |
| IsActive | Disabled realms reject login attempts |

## Creating a realm

::: warning Only realms with CanManageTenants can do this
The "Create" button only appears if your current realm has `CanManageTenants = true`. Defaults: only the system realm.
:::

Admin → **Realms** → **Create**.

| Field | Example |
| --- | --- |
| Slug | `acme` |
| Display Name | `ACME Corp` |
| Description | `Production tenant for ACME` |
| Domains | `acme.auth.firma.at` |
| Can manage tenants | unchecked (the system realm already does that) |

On save, Cocoar.Auth:

1. Validates the slug format (3-63 chars, lowercase, alphanumeric + hyphen)
2. Creates a PostgreSQL database `<main-db>_acme`
3. Registers the realm with Marten's master-table tenancy
4. Applies the schema to the new tenant DB
5. Stores the Realm document in the master DB
6. Seeds default OAuth scopes + the Internal login provider in the new tenant DB
7. Seeds the `cocoar-auth` system app

The realm is now active. Routing requests to one of its domains lands in the new tenant.

## Editing a realm

Most fields are live-editable; the **slug is immutable** (it's baked into the database name).

::: warning Last managing realm
You can't disable or remove `CanManageTenants` from the last realm that has it — otherwise nobody could ever create another realm. The UI prevents this with a clear error.
:::

## Deactivating vs. deleting

- **Deactivate** (clear "Is Active") — the realm rejects logins but stays in the DB. Reactivatable any time.
- **Delete** — soft delete in the master DB. The tenant database is **not** dropped automatically (data preservation by default). Drop the DB manually if you really mean to wipe it.

## First-time setup of a fresh realm

After a new realm is created, no admin user exists yet. Three options:

1. **Auto-create on first request**: route a browser to one of the realm's domains. The setup wizard kicks in (`/setup`) and the first person who completes it becomes the realm admin.
2. **Magic link from the system realm**: as system admin, create a user in the new realm and send a sign-in link.
3. **Recovery CLI**: bootstrap from the container shell — see [Recovery CLI](./recovery-cli).

## Routing

Cocoar.Auth's `RealmMiddleware` resolves the realm from `HttpContext.Request.Host`. Each request finds its realm by matching the host against any realm's `Domains` list.

If a host doesn't match any realm: 404 (the request is for an unrecognised tenant). Recommendation: add `localhost` and `127.0.0.1` to the system realm's domains so dev work stays simple.

## Tips

::: tip Naming conventions
Realm slugs are visible in URLs (`/realms/<slug>`) and are baked into DB names. Pick stable, customer-friendly slugs and stick with them. Avoid changes — slug changes are not supported.
:::

::: tip Data residency
Each realm's data lives in its own PostgreSQL database. For data-residency compliance, you can configure separate database servers per realm via the `RealmProvisioningService` extension hooks (advanced setup, not exposed in the UI today).
:::
