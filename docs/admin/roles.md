# Roles

A **role** bundles permissions for one app. Users receive roles only through their [groups](./groups) — never directly.

![Roles list](/screenshots/admin-rollen-liste.png)

## The permission model

```
User
   ↓ membership (transitive BFS)
Group(s)
   ↓ does BoundTo contain the requesting app?  (otherwise: dormant)
active group(s)
   ↓ roles
Role(s) (with AppSlug)
   ↓ filter: Role.AppSlug == requesting app?  (or permission is fully-qualified)
Permission(s)  →  app:resource:action
```

Effect: a user is `Editor in TimeToDo` because

1. they are a member of a group `TimeToDo Team`,
2. the group has `BoundTo: ["timetodo"]`,
3. the group references a role `TimeToDo Editor` with `AppSlug = "timetodo"`,
4. the role's permissions `read`, `write` on resource `todo` expand to `timetodo:todo:read`, `timetodo:todo:write`.

## Permission format: three segments

Modgud manages permissions as **`app:resource:action`** strings:

| Permission | Meaning |
| --- | --- |
| `modgud:user:read` | Read the user list in modgud |
| `modgud:oauth-client:write` | Edit OAuth clients in modgud |
| `timetodo:todo:write` | Write todos in the TimeToDo app |

Plus three bypass tiers:

- **`realm:admin`** — realm-wide. The holder may do anything in any app.
- **`<app>:admin`** — app-wide.
- **`<app>:<resource>:admin`** — resource-wide.

## Standard roles (after setup)

When the first admin in a realm is created (recovery CLI or HTTP bootstrap-invite — see [First-time setup](../getting-started/first-time-setup)), Modgud atomically seeds three roles — all under the system app `modgud`:

| Role | App | Effect |
| --- | --- | --- |
| **System Admin** | modgud | holds the fully-qualified permission `realm:admin` → realm-wide bypass |
| **User Manager** | modgud | `modgud:user:read/write` + `:session:read/write` + `:authorization-group:read` + `:permission-role:read` + `:auth-log:read` |
| **Viewer** | modgud | read-only on user, authorization-group, permission-role |

Run `node scripts/seed-demo.mjs` after first login and you'll get additional roles for realistic test setups (see `data/demo-seed.json` for the manifest).

## Resources available per app

What resources an app has is defined by the app itself — see [Applications](./applications). The system app `modgud` has these built in:

| Resource | Typical actions |
| --- | --- |
| **app** | read, write, admin (for app management itself) |
| **user** | read, write |
| **session** | read, write |
| **permission-role** | read, write |
| **authorization-group** | read, write |
| **oauth-client** | read, write |
| **oauth-scope** | read, write |
| **oauth-api** | read, write |
| **login-provider** | admin, read, write |
| **idp-config** | read, write |
| **realm** | read, write |
| **auth-log** | read |
| **gdpr** | admin |

External apps (TimeToDo, Knowledge, …) bring their own resources, defined in their App record.

## Creating or editing a role

Administration → **Roles** → **Create**, or double-click an entry.

![Role detail](/screenshots/admin-rolle-detail.png)

Fields:

- **Name** (unique per realm)
- **Description** (optional)
- **AppSlug** — which app does this role belong to? Required. A role belongs to exactly one app.
- **Resource Type** — together with AppSlug determines the permission prefix
- **Permissions** — actions on the resource. With Resource Type `todo` and Permissions `["read", "write"]`, the role resolves to `<AppSlug>:todo:read` and `<AppSlug>:todo:write`.

### Multi-resource roles

If you want a role to span several resources (e.g. "User Manager" covers user, session, authorization-group), **leave Resource Type empty** and write fully-qualified permissions in the list:

```
modgud:user:read
modgud:user:write
modgud:session:read
modgud:authorization-group:read
```

Fully-qualified strings (containing `:`) pass through the resolver unchanged. The seeded System Admin / User Manager / Viewer roles are built exactly this way.

## Cross-app roles (special case)

A role can also include fully-qualified permissions from **other** apps in its permissions list — for example a "Cross-App Auditor" with `modgud:auth-log:read` AND `timetodo:audit:read`. This works because fully-qualified permissions pass through without further filtering.

In practice though: prefer two separate roles in two separate groups (each with their own BoundTo). Cleaner to understand and audit.

## Bypass roles

A role becomes a bypass role when its permissions list contains an `admin`-shaped entry:

| In the permissions list | Effect |
| --- | --- |
| `realm:admin` (fully qualified) | realm-wide bypass |
| `<app>:admin` | app-wide bypass |
| `<app>:<resource>:admin` (Resource Type empty + fully qualified) | resource-wide |
| `admin` (with Resource Type set) | resource-wide, AppSlug-prefixed |

On setup exactly one user is seeded as realm admin (System Admin role + Administratoren group with `BoundTo: ["*"]`). Grant sparingly — realm-admin is the nuclear option.

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
Roles for TimeToDo go under `AppSlug = "timetodo"`, not `modgud`. They show up in the right permission lists, and `[Authorize(Roles = "...")]` in the TimeToDo backend finds them via the `resource_access["timetodo"]` claim in the token.
:::
