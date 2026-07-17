# Realms

A **Realm** in Modgud is a tenant — a fully isolated namespace with
its own database, users, groups, OAuth clients, and apps. Realms are
how multi-tenant Modgud deployments separate customers / environments
/ staging.

::: info When do I need multiple realms?
- **Multiple customers** sharing one Modgud instance (each gets their
  own realm)
- **Stage separation** (production, staging, development) on shared
  infrastructure
- **Compliance isolation** (some customer data must not coexist in
  the same DB)

Single-tenant deployments only need the system realm — provisioned
automatically on first start.
:::

![Realm list](/screenshots/admin-realms-liste.png)

## The Control-Plane realm

Exactly **one** realm in a deployment is the **Control Plane** — the
realm flagged `IsControlPlane = true`. The Control Plane is the only
host where realm CRUD is exposed; tenant realms get a 404 even from a
user that somehow holds `realm:read`/`realm:write` (those catalog
entries don't exist in their tenant DB because the `control-plane` app
isn't seeded there). See
[Concepts: Control Plane / Data Plane](../concepts/control-plane) for
the full three-layer defence.

The Control-Plane flag is a **stored, transferable** field. The `system`
realm is stamped as the CP at first boot, but the role can be moved to any
active realm (see [Transferring the control plane](#transferring-the-control-plane)).
There is always exactly one CP per deployment.

The system realm's default domains are `system.localhost`,
`localhost`, `127.0.0.1` — anything resolving to those lands on the
system realm.

## Realm fields

| Field | Meaning |
| --- | --- |
| Slug | URL-safe identifier, 3-63 chars, immutable. Determines the tenant DB name (`<main-db>_<slug>`). |
| Display Name | UI label |
| Description | Optional |
| Domains | List of hostnames that route to this realm |
| Primary Domain | The realm's canonical public host — one of `Domains`. Used for every outbound link (magic-links, bootstrap-invites) and as the WebAuthn relying-party ID for passkeys. Changing it invalidates existing passkeys. |
| IsControlPlane | Stored flag — exactly one realm holds it. Moved via the transfer action, not edited inline. |
| IsActive | Disabled realms reject login attempts |

## Permissions

Realm-CRUD endpoints under `/api/admin/realms/*` are gated by
permissions in the `control-plane` app's catalog:

| Permission | Effect |
|---|---|
| `realm:read` (control-plane) | List + read realms |
| `realm:write` (control-plane) | Create / edit / deactivate realms |

These permissions only exist on the Control-Plane realm because the
`control-plane` App catalog is only seeded there. The realm-wide
bypass `realm:admin` grants all of them.

## Creating a realm

::: warning Only available on the Control-Plane realm
The "Create" button only appears when you're signed in on the
Control-Plane host. From a tenant host the realm-management surface
is 404.
:::

::: tip Realm-as-code / per-test realms
To create (or update, or tear down) a **complete** realm — apps, OAuth
clients/scopes/APIs, roles, users, groups and settings — from a single JSON
manifest in one call, see [Declarative Realm Provisioning](./realm-provisioning).
Ideal for reproducible setups, per-test realms, and automation. It also serves a
JSON Schema of the manifest you (or an agent) can fetch to author it.
:::

Admin → **Realms** → **Create**.

| Field | Example |
| --- | --- |
| Slug | `acme` |
| Display Name | `Acme Corp` |
| Description | `Production tenant for Acme` |
| Domains | `acme.auth.example.com` |
| Primary Domain | `acme.auth.example.com` — defaults to the first domain; pick which one is canonical when a realm has several |
| **Initial admin** | **required** — UserName + Email of the recipient who'll bootstrap the realm |

The Initial-Admin block is mandatory. A realm with no admin path
would be unreachable; the UI rejects the form if either UserName or
Email is empty.

On save, Modgud:

1. Validates the slug format (3-63 chars, lowercase, alphanumeric +
   hyphen).
2. Creates a PostgreSQL database `<main-db>_acme`.
3. Registers the realm with Marten's master-table tenancy and
   applies the schema.
4. Stores the Realm document in the master DB.
5. Seeds the 6 default OAuth scopes + the Internal login provider in
   the new tenant DB.
6. Seeds the `modgud` app (the realm-internal admin surface). The
   `control-plane` app is **not** seeded into a tenant realm — it
   only exists in the Control-Plane realm.
7. Issues a **bootstrap-invite** for the Initial-Admin: writes a
   single-use, 7-day token into the new tenant DB and sends a
   magic-link email. The magic-link URL is also returned in the API
   response so you can copy it manually if SMTP isn't reachable.

The recipient clicks the magic link, lands on `/bootstrap?token=…`
in the new realm's SPA, sets their own password, and is auto-signed-in.
The token is revoked on first use.

If the link gets lost (expired, deleted, never delivered), open the
realm in the admin UI and click **Resend invite** — a fresh token is
issued for the same recipient and the previous one is revoked.

## Editing a realm

Most fields are live-editable; the **slug is immutable** (it's baked
into the database name). The Control-Plane flag isn't a checkbox — it
moves via the dedicated transfer action (below).

::: warning Changing the Primary Domain invalidates passkeys
The Primary Domain is the WebAuthn relying-party ID. Re-pointing it (in the domain picker, or via the [Recovery CLI](../operate/recovery-cli) `realm-set-primary-domain`) **invalidates every passkey registered in the realm** — affected users must re-register theirs on next sign-in. Password, TOTP, Email OTP, and magic-link logins are unaffected.
:::

## Transferring the control plane

To hand cross-realm administration to another realm, open the **target**
realm (the one that should become the CP) in the admin UI and click
**Make this realm the control plane** (a danger action shown in edit mode for
active, non-CP realms). After you confirm:

- the target realm's `realm:admin` users gain the realm-management surface;
- **this** host stops being the control plane — `/api/admin/realms` 404s here
  and the realm grid disappears. Continue administration on the target realm's
  domain.

Make sure the target realm already has a `realm:admin` user before
transferring, or recover one afterwards via the
[Recovery CLI](../operate/recovery-cli) (`control-plane transfer` /
`bootstrap-admin`).

## Deactivating vs. deleting

- **Deactivate** (clear "Is Active") — the realm rejects logins but
  stays in the DB. Reactivatable any time. Cannot deactivate the
  Control-Plane realm (`Realm.CannotDeactivateControlPlane`).
- **Delete** — soft delete in the master DB by default. The tenant database is
  **not** dropped automatically (data preservation by default), so a plain
  delete is reversible at the database level. To wipe a realm for real, hard
  delete it: `DELETE /api/admin/realms/{slug}?hard=true` drops the tenant
  database and removes the realm record (Control-Plane only, irreversible) —
  see [Declarative Realm Provisioning](./realm-provisioning) for the API surface.

## First-time setup of a fresh realm

The realm is provisioned together with a bootstrap-invite for the
Initial Admin (above). The invite recipient clicks the magic-link and
sets their password — that's the standard path for nearly every
realm.

If something goes wrong:

- **Token lost or expired** — reopen the realm in the admin UI and
  click **Resend invite**. Same recipient, fresh token.
- **No prior invite, no admin yet** (e.g. provisioned via a tool
  that didn't issue one) — drop into the container and run
  `dotnet Modgud.Api.dll recover bootstrap-admin --email <e> --realm <slug>`.
  See [Recovery CLI](../operate/recovery-cli).
- **Locked-out admin** — same recovery CLI, again with
  `bootstrap-admin --email <e>`. The CLI adds the new user to the
  realm's existing admin group rather than creating a duplicate.

## Routing

Modgud's `RealmMiddleware` resolves the realm from
`HttpContext.Request.Host`. Each request finds its realm by matching
the host against any realm's `Domains` list.

If a host doesn't match any realm: 404 (the request is for an
unrecognised tenant). For dev work without hosts-file edits, the
system realm's default `Domains` list includes `localhost` and
`127.0.0.1` — the single-realm fallback in `RealmCache` also catches
localhost variants when only one realm is active.

## Tips

::: tip Naming conventions
Realm slugs are baked into the tenant DB name and the default Domains
list (`<slug>.localhost`). Pick stable, customer-friendly slugs and
stick with them. Slug changes are not supported.
:::

::: tip Data residency
Each realm's data lives in its own PostgreSQL database. For
data-residency compliance, you can configure separate database
servers per realm via the `RealmProvisioningService` extension hooks
(advanced setup, not exposed in the UI today).
:::
