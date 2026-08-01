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

Single-tenant deployments only need the first realm created during
[first installation](../getting-started/first-time-setup).
:::

![Create realm dialog](/screenshots/admin-realm-modal.png)

## The Control-Plane realm

Exactly **one** realm in a deployment is the **Control Plane** — the
realm flagged `IsControlPlane = true`. The Control Plane is the only
host where realm CRUD is exposed; tenant realms get a 404 even from a
user that somehow holds `realm:read`/`realm:write` (those catalog
entries don't exist in their tenant DB because the `control-plane` app
isn't seeded there). See
[Concepts: Control Plane / Data Plane](../concepts/control-plane) for
the full three-layer defence.

The Control-Plane flag is a **stored, transferable** field. First installation
stamps the first ordinary realm as the CP; the role can later move to any
active realm (see [Transferring the control plane](#transferring-the-control-plane)).
There is exactly one CP after installation, and no slug has special meaning.

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
7. Finishes the realm creation. Creating a realm and inviting an
   administrator are deliberately separate actions.

To add an administrator, open the realm's context menu and choose
**Realm-Admin einladen**. The recipient clicks the magic link, lands
on `/bootstrap?token=…` in the realm's SPA, sets their own password,
and is auto-signed-in.

Only one admin invitation can be open in a realm. A new invitation
revokes the previous link, is valid for 24 hours, and can be used once.

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

## Inviting a realm administrator

A realm is complete and active as soon as realm creation finishes; it does not
need an administrator to be valid. When someone should manage it, use
**Invite realm admin** in the realm's context menu. The recipient clicks the
magic link and sets their password.

If something goes wrong:

- **Token lost or expired** — issue a new invitation. It automatically revokes
  the previous open link.
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

If a host doesn't match any realm, the request returns 404. Register every
hostname explicitly in the realm's Domains list. `*.localhost` resolves to
loopback on modern browsers and operating systems, but Modgud still needs the
exact host-to-realm mapping.

## Tips

::: tip Naming conventions
Realm slugs are baked into the tenant DB name. Pick stable,
customer-friendly slugs and
stick with them. Slug changes are not supported.
:::

::: tip Data residency
Each realm's data lives in its own PostgreSQL database. For
data-residency compliance, you can configure separate database
servers per realm via the `RealmProvisioningService` extension hooks
(advanced setup, not exposed in the UI today).
:::
