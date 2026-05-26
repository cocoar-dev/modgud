# Applications

An **Application** in Modgud is the organisational clamp around a SaaS
app — it owns its own permission catalog, its own roles, and its own
OAuth bindings. When a realm is created the system app `modgud` (=
Modgud itself) is provisioned automatically; every other app you
register here.

::: tip First time?
If this is your first integration, the [SaaS App Integration Walkthrough](../integrate/saas-walkthrough)
is the better entry point — it walks through all five stations (App,
Client, Resource Server, Roles, backend code).
:::

## What is an Application for?

Modgud manages permissions as **two-segment** `<resource>:<action>`
strings inside an App's catalog — the app slug is the implicit context,
not part of the string. The same string `invoice:write` in the `billing`
app's catalog and in the `shipping` app's catalog are different
permissions, distinguished by the audience the gate is running for.

An app therefore bundles:

- **Permission catalog** — the `<resource>:<action>` entries that the
  app's resources understand
- **Roles** with `AppId` — bundles of `PermissionIds` from this app's
  catalog
- **Groups** via `BoundTo` — which organisational unit is active in
  which app
- **OAuth Clients** via their `AppIds` list — which token requesters
  serve the app
- **OAuth APIs (Resource Servers)** via their `AppId` — which backend
  identities belong to it
- **OAuth Scopes** via their `AppId` — which scopes a client of the
  app may request

## Application fields

| Field | Meaning |
| --- | --- |
| Slug | URL- and permission-safe identifier. Lowercase, 3-63 characters, letters/digits/hyphens. **Immutable after creation.** |
| Display Name | What appears in lists and consent screens |
| Description | Optional, one-liner |
| Permission catalog | `<resource>:<action>` entries this app's resources can be gated by |
| IsSystem | True only for `modgud` and `control-plane`; cannot be deleted |

## Reserved slugs

These slugs are forbidden — they collide with the permission grammar
or with system invariants:

- `realm` — would clash with `realm:admin` (realm-wide bypass)
- `*` — wildcard in `Group.BoundTo`
- `modgud` — system app, seeded automatically into every realm
- `control-plane` — control-plane system app, seeded only on the
  Control-Plane realm

## Creating an app

Click **Create** in the list view.

1. Pick a slug — kebab-case, memorable: `acme`, `billing`,
   `inventory`. Not changeable.
2. Fill in display name and description.
3. Add catalog entries: `<resource>:<action>`, kebab-case both sides
   (`invoice:read`, `invoice:write`, `invoice:admin`).
4. **Create**.

The app appears in the list. **It still has no effect** on its own —
you also need to:

- link at least one OAuth client to it ([OAuth Clients](./oauth-clients))
- (for an authenticated server-to-server callback into Modgud)
  provision a resource server
- create at least one role + group that connects users to the app

## Provisioning the resource server

Under [OAuth → APIs](./oauth-apis), create an OAuth API named after
the app's slug and link it to the app. Its `PermissionIds` declare
which subset of the catalog this resource server gates on (full
catalog is the typical default; tighten for microservices that only
need a slice). This is the identity Modgud uses to compute the
per-Audience `resource_access` block in UserInfo.

## Extending or changing the catalog

Catalog entries can be edited any time, but:

- **Adding** is harmless. Existing role assignments remain valid; new
  permissions become assignable.
- **Removing** is dangerous. Roles that reference the removed entry
  silently lose the grant. The admin UI shows a "rename" indicator and
  a delete-block prompt when something downstream is still referencing
  a catalog entry. Audit roles before dropping.

## Relationships to other areas

| Linked with | Where | How |
| --- | --- | --- |
| OAuth Clients | [OAuth Clients](./oauth-clients) | n:m via the client's `AppIds` list |
| OAuth Scopes | [OAuth Scopes](./oauth-scopes) | 1:n via the scope's `AppId` (or null = global) |
| OAuth APIs (Resource Servers) | [OAuth APIs](./oauth-apis) | 1:n via the API's `AppId` |
| Roles | [Roles](./roles) | n:1 via the role's `AppId` |
| Groups | [Groups](./groups) | n:m via the group's `BoundTo` list |

## The system apps

### `modgud`
The app `modgud` represents the Modgud admin surface itself.
Permissions like `user:read` or `oauth-client:write` (in this app's
catalog) gate the admin UI sidebar.

- **Auto-seeded** on first realm setup
- **Not deletable** (`IsSystem = true`)
- **Slug not renameable**
- Catalog matches the built-in admin surface — edit cautiously

### `control-plane`
Seeded **only** into the realm flagged `IsControlPlane = true`. Owns
the `realm:read` / `realm:write` permissions that gate
`/api/admin/realms/*`. See
[Concepts: Control Plane / Data Plane](../concepts/control-plane).

If you change a system app's catalog, the admin sidebar may hide items
because the corresponding permission is no longer registered. When in
doubt, restore the default catalog (see `AppRealmSeeder` in source).

## Deleting an app

System apps cannot be deleted. Regular apps can — but:

- OAuth clients with the app in their `AppIds` list keep the entry
  (UI shows it as "unknown app")
- OAuth scopes with this AppId become orphaned
- Roles with this AppId stay — but their `PermissionIds` no longer
  resolve to anything
- Groups with the app in BoundTo keep the entry, but it no longer has
  effect

So before deleting: re-link or delete the dependent clients, scopes,
and roles first.
