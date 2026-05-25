# Authorization (RBAC)

Modgud is a pure **RBAC + grouping** Identity & Access Management system. It answers `(user, app, permission)` — nothing more.

Row-level access policies (ABAC) deliberately stay **outside** the IAM and live in the consuming app where the row schema is. See [ABAC and the IAM boundary](./abac) for the rationale and the three deployment profiles.

## RBAC: User → Group → Role → Permission

Permissions flow exclusively through groups:

```
User ──► Group ──► PermissionRole ──► "<app>:<resource>:<action>"
```

There are no direct `User → Role` assignments and no `User → Permission` overrides. The resolution path:

1. Find every group the user is in (transitively, including nested groups).
2. Filter to groups whose `BoundTo` includes the requested app (or the `*` wildcard).
3. Collect the role ids on those groups.
4. Filter to roles whose `AppSlug` matches the requested app.
5. Expand each role's actions to fully-qualified `app:resource:action` permissions.
6. Apply the bypass tiers below.

## Permission format

Three segments, slash-free:

```
<app>:<resource>:<action>
```

| Example | Meaning |
| --- | --- |
| `modgud:user:read` | Read users in the IAM admin app |
| `modgud:oauth-client:write` | Manage OAuth clients in the IAM admin app |
| `timetodo:todo:write` | Create/update todos in the TimeToDo app |
| `realm:admin` | Realm-wide bypass — everything in every app |
| `<app>:admin` | App-wide bypass for that app |
| `<app>:<resource>:admin` | Resource-wide bypass for that app+resource |

`hasPermission(needed)` returns true iff:

1. the user holds `needed` directly, **or**
2. the user holds `<app>:<resource>:admin` for the same app+resource, **or**
3. the user holds `<app>:admin` for the same app, **or**
4. the user holds `realm:admin`.

The `realm:admin` bypass is intentionally narrow — only the System Admin default role carries it.

## Apps and BoundTo

The IAM hosts an arbitrary number of consuming apps in one realm; each is identified by a slug (`modgud`, `timetodo`, `knowledge`, …). Permissions and roles are app-scoped.

A group's `BoundTo` field is the **activation switch**: it lists the app slugs in which the group's roles take effect.

| BoundTo | Effect |
| --- | --- |
| `["*"]` | Wildcard — active in every app. Typical for the realm-admin group. |
| `["timetodo"]` | Roles only contribute when a `timetodo:*` permission is being resolved. |
| `["timetodo", "knowledge"]` | Active in both apps; same role assignments contribute in either resolution. |
| `[]` | Dormant — the group exists for organisational/mailing purposes only and contributes no permissions. |

Removing an app from a group's BoundTo is a non-destructive deactivation: role assignments stay; re-adding the app reactivates them immediately.

## Default roles per realm

| Role | Permissions |
| --- | --- |
| **System Admin** | `realm:admin` |
| **User Manager** | `modgud:user:read/write`, `modgud:permission-role:read`, `modgud:authorization-group:read/write` |
| **Viewer** | Read-only on Users, Roles, Groups, OAuth-Clients, OAuth-Scopes |

The first-time-setup admin lands in the System Admin group with `BoundTo: ["*"]`, so they immediately see every app.

## Groups

`Group` is the carrier of permissions. A group has:

- `Name`, `Description`
- `MembershipMode` — `Manual` or `Auto`
- `MemberIds` — users or other groups (nested)
- `RoleIds` — references to `PermissionRole`s
- `BoundTo` — app slugs in which the group is active (see above)
- Optional: `MembershipScript` (when membership is Auto)
- Optional: `Email` + `EmailMode` for distribution-list semantics

### Manual vs Auto

- **Manual** — the admin maintains `MemberIds` directly.
- **Auto** — a JsEval predicate (`MembershipScript`) decides which principals match. Re-evaluated on every relevant principal mutation; dependency-tracking skips re-runs when the changed property doesn't appear in the script.

The membership script only sees IAM-owned fields (`DisplayName`, `Email`, `IsActive`, `ExternalIdentities`, `AccountName`). It must not — and cannot — read app-specific schema; that would re-couple the IAM to every consumer's schema. See [ABAC and the IAM boundary](./abac).

### Nested groups

A group can contain other groups. The permission-resolution BFS treats them polymorphically (`IPrincipalWithMembers`), with cycle-detection via a visited set.

```
"All Staff" (Manual)
  ├── "Engineering" (Auto: matches engineers)
  ├── "Sales"       (Auto: matches sales)
  └── "Support"     (Auto: matches support)
```

## What this architecture is *not*

- **No deny rules.** Only positive grants; effective access is the union over all the user's groups.
- **No implicit grants.** Group membership grants nothing on its own; roles must be explicitly assigned.
- **No direct user-to-role.** Everything routes through groups.
- **No row-level rules.** ABAC stays in the app; the IAM keeps `(user, app, permission)` as its sole answer surface.

## Sidebar mirror

The Vue admin shell mirrors the same logic 1:1: each sidebar item declares the permission it requires, the backend evaluates the same string. The single source of truth is the permission constant — frontend gating cannot drift from backend gating because both consult the identical literal.

```ts
{ section: 'authorization', label: 'nav.users', icon: 'users',
  path: '/admin/users', requirePermissions: ['modgud:user:read'] }
```

A user with only `modgud:user:read` sees just "Users" in the sidebar — no OAuth, no System.
