# Roles

An **application role** bundles permissions for exactly one app. A pure
`realm:admin` role is the explicit exception: it has no Application link or
catalog permissions and grants the bypass across every app in its own realm.
Users receive roles only through their [groups](./groups) — never directly.

![Roles list](/screenshots/admin-rollen-liste.png)

## The permission model

```
User
   ↓ membership (transitive BFS)
Group(s)
   ↓ does BoundTo contain the requesting app?  (otherwise: dormant)
active group(s)
   ↓ roles
Role(s) (linked to one Application)
   ↓ filter: Role.AppId == requesting app?  (or the role is a realm-admin role)
Permission(s)  →  resource:action
```

Effect: a user is `Editor in Acme-Tasks` because

1. they are a member of a group `Acme-Tasks Team`,
2. the group has `BoundTo: ["acme-tasks"]`,
3. the group references a role `Acme-Tasks Editor` linked to the Acme-Tasks Application,
4. the role grants the catalog entries `todo:read` and `todo:write` from that Application.

## Permission format: two segments

Modgud manages permissions as **`resource:action`** strings, scoped to
whichever Application the role belongs to (see
[Concepts → Permissions & gating](../concepts/permissions) for the
full model):

| Permission | Meaning |
| --- | --- |
| `user:read` | Read the user list — in whichever app the role is linked to |
| `oauth-client:write` | Edit OAuth clients — same |
| `todo:write` | Write todos — same, if granted on an Acme-Tasks-linked role |

The app is never part of the string — it comes from the role's
Application link (or, for the built-in `modgud`/`control-plane` admin
surfaces, from the endpoint being called). Plus two bypass tiers:

- **`realm:admin`** — current-realm-wide. The holder may do anything in any app in this realm, but gains nothing in another realm. It is represented by a pure realm-admin role, not a catalog entry.
- **`<resource>:admin`** — resource-wide, within the role's linked Application (e.g. `user:admin` bypasses both `user:read` and `user:write`).

There is no app-wide bypass tier — bypass is either realm-wide or resource-wide, nothing in between.

## Standard roles (after setup)

When the first admin in a realm is created (recovery CLI or HTTP bootstrap-invite — see [First-time setup](../getting-started/first-time-setup)), Modgud atomically seeds three roles:

| Role | Application link | Effect |
| --- | --- | --- |
| **System Admin** | none — **Privileged role** flag set | realm-wide bypass (`realm:admin`) |
| **User Manager** | modgud | `user:read/write` + `session:read/write` + `authorization-group:read` + `permission-role:read` + `auth-log:read` + `audit-log:read` |
| **Viewer** | modgud | read-only on `user`, `authorization-group`, `permission-role` |

Run `node scripts/seed-demo.mjs` after first login and you'll get additional roles for realistic test setups (see `data/demo-seed.json` for the manifest).

## Resources available per app

What resources an app has is defined by the app itself — see [Applications](./applications). The system app `modgud` has these built in:

| Resource | Typical actions |
| --- | --- |
| **app** | read, write, admin (for app management itself) |
| **user** | read, write |
| **service-account** | read, write |
| **role** | read, write |
| **authorization-group** | read, write |
| **permission-role** | read, write |
| **session** | read, write |
| **auth-log** | read |
| **audit-log** | read |
| **gdpr** | admin |
| **oauth** | admin |
| **oauth-client** | read, write |
| **oauth-scope** | read, write |
| **oauth-api** | read, write |
| **login-provider** | admin, read, write |
| **realm-settings** | read, write |
| **asset** | read, write |
| **observability** | read |
| **scheduled-job** | read, write |
| **inbox-settings** | read, write |

The **realm** resource (realm CRUD) lives in a separate `control-plane`
app, seeded only into the control-plane realm — see
[Realms](./realms) — not in `modgud`.

External apps (Acme-Tasks, Knowledge, …) bring their own resources, defined in their App record.

## Creating or editing a role

Administration → **Roles** → **Create**, or double-click an entry.

![Role detail](/screenshots/admin-rolle-detail.png)

The modal has two tabs:

**General**

- **Name** (unique per realm)
- **Description** (optional)
- **Application** — which app does this role belong to? Pick "— None
  (realm-admin role)" only for a pure bypass role; otherwise a role
  belongs to exactly one Application.
- **Privileged role** — switches the role into the pure realm-admin mode.
  Enabling it clears and disables the Application link and catalog
  permissions. It grants `realm:admin` in this realm only and is reserved
  for the System Admin role.

**Permissions**

A checklist of the linked Application's permission catalog, one row
per `resource:action` entry. Check as many as the role should grant —
there's no per-resource limit, so a role can (and often does) span
several resources of the same app in one go.

### Multi-resource roles

A role isn't limited to one resource. The seeded **User Manager** role,
for example, checks entries across `user`, `session`,
`authorization-group`, `permission-role`, `auth-log` and `audit-log` —
all from the `modgud` catalog, all on one role.

## Cloning a role

To make a variant of a role — say a tighter copy of an existing one — right-click it in the list → **Clone**. The Create modal opens pre-filled: for an application role, the linked Application and selected permission subset are copied; for a realm-admin role, only the pure realm-admin mode is copied. The **Name** is blank. Give the copy a new name, adjust the selection, and create.

## Cross-app roles (special case)

A role is always linked to exactly one Application (or none, for a
pure realm-admin role) — there's no way to check permissions from two
different apps' catalogs on the same role. To grant someone rights in
both, say, `modgud` and Acme-Tasks, create two roles (one per app) and
put both in the same group — or in two groups, if you also want their
`BoundTo` scoping to differ. Cleaner to understand and audit than a
single sprawling role either way.

## Bypass roles

A role becomes a bypass role through either of two mechanisms:

| Mechanism | Effect |
| --- | --- |
| Pure **Privileged role** | current-realm-wide bypass (`realm:admin`) — works in every app in this realm and has no Application link |
| A catalog entry with action `admin` checked (e.g. `user:admin`) | resource-wide bypass — every action on that resource, within the role's linked Application |

There's no app-wide bypass in between — a role is either realm-wide or scoped down to individual resources.

On setup exactly one user is seeded as realm admin (System Admin role + the realm's admin group, `BoundTo: ["*"]`). Grant sparingly — realm-admin is the nuclear option.

## Deleting a role

List → right-click → **Delete**.

::: warning Soft delete
Roles are soft-deleted. Groups that referenced the role keep the entry technically — but the role contributes no permissions any more. To remove a role cleanly, remove it from all groups first.
:::

## Tips

::: tip Keep roles narrow
Many small roles, each tied to a clear resource, compose freely into groups. A "SuperAdmin" role with every permission is usually a design smell; use `realm:admin` for that, or combine specialised roles in an admin group.
:::

::: tip Per-app roles
Roles for Acme-Tasks link to the Acme-Tasks Application, not `modgud`.
If its backend is registered with Audience `acme-tasks-api`, then
`[Authorize(Roles = "...")]` finds them through
`resource_access["acme-tasks-api"].roles` when the token targets that
API and the `roles` scope was granted.
:::
