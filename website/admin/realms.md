# Realms

A **Realm** in Cocoar.Auth is a tenant — a fully isolated namespace with its own database, users, groups, OAuth clients, and apps. Realms are how multi-tenant Cocoar.Auth deployments separate customers / environments / staging.

::: info When do I need multiple realms?
- **Multiple customers** sharing one Cocoar.Auth instance (each gets their own realm)
- **Stage separation** (production, staging, development) on shared infrastructure
- **Compliance isolation** (some customer data must not coexist in the same DB)

Single-tenant deployments only need the system realm — provisioned automatically on first start.
:::

![Realm list](/screenshots/admin-realms-liste.png)

## The Control-Plane realm

Exactly **one** realm in a deployment is the **Control Plane** — the realm flagged `IsControlPlane = true`. The Control Plane is the only host where realm CRUD is exposed; tenant realms get a 404 even if they're somehow handed a `control-plane:realm:write` permission. See [Concepts: Control Plane / Data Plane](../concepts/control-plane) for the full three-layer defence.

The first realm, with slug `system`, is created automatically by `EnsureSystemRealmExistsAsync` on app startup and starts as the Control Plane. You can move the flag to a different realm later (with the deployment's blessing — the API enforces "exactly one CP at a time"), but most installations leave it on `system`.

The system realm's default domains are `system.localhost`, `localhost`, `127.0.0.1` — anything resolving to those lands on the system realm.

## Realm fields

| Field | Meaning |
| --- | --- |
| Slug | URL-safe identifier, 3-63 chars, immutable. Determines the tenant DB name (`<main-db>_<slug>`). |
| Display Name | UI label |
| Description | Optional |
| Domains | List of hostnames that route to this realm |
| IsControlPlane | If true, this is the deployment's Control-Plane realm. Exactly one allowed. |
| IsActive | Disabled realms reject login attempts |

## Creating a realm

::: warning Only available on the Control-Plane realm
The "Create" button only appears when you're signed in on the Control-Plane host. From a tenant host the realm-management surface is 404.
:::

Admin → **Realms** → **Create**.

| Field | Example |
| --- | --- |
| Slug | `acme` |
| Display Name | `ACME Corp` |
| Description | `Production tenant for ACME` |
| Domains | `acme.auth.firma.at` |
| Control Plane | unchecked (the system realm already is) |
| **Initial admin** | **required** — UserName + Email of the recipient who'll bootstrap the realm |

The Initial-Admin block is mandatory. A realm with no admin path would be unreachable; the UI rejects the form if either UserName or Email is empty.

On save, Cocoar.Auth:

1. Validates the slug format (3-63 chars, lowercase, alphanumeric + hyphen) and the exactly-one-CP invariant.
2. Creates a PostgreSQL database `<main-db>_acme`.
3. Registers the realm with Marten's master-table tenancy and applies the schema.
4. Stores the Realm document in the master DB.
5. Seeds default OAuth scopes + the Internal login provider in the new tenant DB.
6. Seeds the `cocoar-auth` app (the realm-internal admin surface). The `control-plane` app is **not** seeded into a tenant realm — it only exists in the Control-Plane realm.
7. Issues a **bootstrap-invite** for the Initial-Admin: writes a single-use, 7-day token into the new tenant DB and sends a magic-link email. The magic-link URL is also returned in the API response so you can copy it manually if SMTP isn't reachable.

The recipient clicks the magic link, lands on `/bootstrap?token=…` in the new realm's SPA, sets their own password, and is auto-signed-in. The token is revoked on first use.

If the link gets lost (expired, deleted, never delivered), open the realm in the admin UI and click **Resend invite** — a fresh token is issued for the same recipient and the previous one is revoked.

## Editing a realm

Most fields are live-editable; the **slug is immutable** (it's baked into the database name).

::: warning The Control-Plane flag is a two-step hand-off
You can move the Control-Plane flag from one realm to another, but you have to **promote** the new CP first, then **demote** the old one — never both in a single PATCH. The API enforces both invariants:

- Demoting the last CP returns `400 Realm.CannotRemoveControlPlaneFlag` — no other realm holds the flag.
- Promoting a second CP returns `400 Realm.ControlPlaneAlreadyExists` — the existing one has to be demoted first.
:::

## Deactivating vs. deleting

- **Deactivate** (clear "Is Active") — the realm rejects logins but stays in the DB. Reactivatable any time. Cannot deactivate the last Control-Plane realm.
- **Delete** — soft delete in the master DB. The tenant database is **not** dropped automatically (data preservation by default). Drop the DB manually if you really mean to wipe it.

## First-time setup of a fresh realm

The realm is provisioned together with a bootstrap-invite for the Initial Admin (above). The invite recipient clicks the magic-link and sets their password — that's the standard path for nearly every realm.

If something goes wrong:

- **Token lost or expired** — reopen the realm in the admin UI and click **Resend invite**. Same recipient, fresh token.
- **No prior invite, no admin yet** (e.g. you provisioned the realm via a tool that didn't issue one) — drop into the container and run `dotnet Cocoar.Auth.Api.dll recover bootstrap-admin --email <e> --realm <slug>`. See [Recovery CLI](./recovery-cli).
- **Locked-out admin** — same recovery CLI, again with `bootstrap-admin --email <e>`. The CLI adds the new user to the existing Administratoren group rather than creating a duplicate.

## Routing

Cocoar.Auth's `RealmMiddleware` resolves the realm from `HttpContext.Request.Host`. Each request finds its realm by matching the host against any realm's `Domains` list.

If a host doesn't match any realm: 404 (the request is for an unrecognised tenant). For dev work without hosts-file edits, the system realm's default `Domains` list includes `localhost` and `127.0.0.1` — the single-realm fallback in `RealmCache` also catches localhost variants when only one realm is active.

## Tips

::: tip Naming conventions
Realm slugs are baked into the tenant DB name and the default Domains list (`<slug>.localhost`). Pick stable, customer-friendly slugs and stick with them. Slug changes are not supported.
:::

::: tip Data residency
Each realm's data lives in its own PostgreSQL database. For data-residency compliance, you can configure separate database servers per realm via the `RealmProvisioningService` extension hooks (advanced setup, not exposed in the UI today).
:::
