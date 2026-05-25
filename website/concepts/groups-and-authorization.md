# Authorization (RBAC)

Modgud is a pure **RBAC + grouping** Identity & Access Management
system. It answers `(user, app, permission)` — nothing more.

Row-level access policies (ABAC) deliberately stay **outside** the IAM
and live in the consuming app where the row schema is. See
[ABAC and the IAM boundary](./abac) for the rationale and the three
deployment profiles.

## RBAC: User → Group → Role → Permission

Permissions flow exclusively through groups:

```
User ──► Group ──► PermissionRole ──► "<resource>:<action>"
                                       within an App catalog
```

There are no direct `User → Role` assignments and no
`User → Permission` overrides. The resolution path:

1. Find every group the user is in (transitively, including nested
   groups).
2. Filter to groups whose `BoundTo` includes the requested app (or
   the `*` wildcard).
3. Collect the role ids on those groups.
4. Filter to roles whose `AppId` matches the requested app — plus any
   role with `IsRealmAdmin = true`.
5. Resolve each role's `PermissionIds` → catalog strings.
6. Bypass-pre-expand and run the evaluator below.

## Permission format

Permission strings inside an App's catalog are **two segments**:

```
<resource>:<action>
```

The App context is implicit from the catalog container — the string
itself never carries an app slug. When the resolver sweeps a user's
effective permissions for a given app, it works against that App's
catalog; when UserInfo emits a per-Audience block, the audience
determines which app's catalog applies.

| Example | Meaning |
| --- | --- |
| `user:read` | Read users (in whichever App's catalog defines it) |
| `oauth-client:write` | Manage OAuth clients |
| `invoice:read` | Read invoices (e.g. in a `billing` app's catalog) |
| `realm:admin` | Realm-wide bypass — everything in every app |
| `<resource>:admin` | Resource-wide bypass for that resource |

### Bypass tiers — exactly two

`PermissionEvaluator.Evaluate(grants, needed)` returns true when:

1. the user holds `realm:admin`, **or**
2. the user holds `needed` directly, **or**
3. the user holds `<resource>:admin` for the same resource.

There is **no app-wide bypass tier** (`<app>:admin`). Bypass is either
realm-wide or resource-wide; nothing in between. `realm:admin` is
intentionally narrow — only the System Admin default role carries it.

For the full evaluator + emission story (per-Audience UserInfo,
bypass-pre-expansion, per-RS subset narrowing) see the canonical
[Permissions reference](../dev-notes/authorization-slice/permissions).

## Apps and BoundTo

The IAM hosts an arbitrary number of consuming apps in one realm; each
is identified by a slug (`modgud`, `acme`, `billing`, …).
PermissionRoles bind to one app (via `AppId`); groups carry an
activation list (via `BoundTo`).

A group's `BoundTo` field is the **activation switch**: it lists the
app slugs in which the group's roles take effect.

| BoundTo | Effect |
| --- | --- |
| `["*"]` | Wildcard — active in every app. Typical for the realm-admin group. |
| `["acme"]` | Roles only contribute when an `acme`-scoped permission is being resolved. |
| `["acme", "billing"]` | Active in both apps; same role assignments contribute in either resolution. |
| `[]` | Dormant — the group exists for organisational/mailing purposes only and contributes no permissions. |

Removing an app from a group's BoundTo is a non-destructive
deactivation: role assignments stay; re-adding the app reactivates
them immediately.

## Default roles per realm

| Role | Permissions |
| --- | --- |
| **System Admin** | `IsRealmAdmin = true` (the realm-wide bypass) |
| **User Manager** | `user:read`, `user:write`, `permission-role:read`, `authorization-group:read`, `authorization-group:write` (in the `modgud` app) |
| **Viewer** | Read-only on Users, Roles, Groups, OAuth-Clients, OAuth-Scopes (in the `modgud` app) |

The first-time-setup admin lands in the System Admin group with
`BoundTo: ["*"]`, so they immediately see every app.

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
- **Auto** — a JsEval predicate (`MembershipScript`) decides which
  principals match. Re-evaluated on every relevant principal
  mutation; dependency-tracking skips re-runs when the changed
  property doesn't appear in the script.

The membership script only sees IAM-owned fields (`DisplayName`,
`Email`, `IsActive`, `ExternalIdentities`, `AccountName`). It must
not — and cannot — read app-specific schema; that would re-couple the
IAM to every consumer's schema. See
[ABAC and the IAM boundary](./abac).

### Nested groups

A group can contain other groups. The permission-resolution BFS treats
them polymorphically (`IPrincipalWithMembers`), with cycle-detection
via a visited set.

```
"All Staff" (Manual)
  ├── "Engineering" (Auto: matches engineers)
  ├── "Sales"       (Auto: matches sales)
  └── "Support"     (Auto: matches support)
```

## What this architecture is *not*

- **No deny rules.** Only positive grants; effective access is the
  union over all the user's groups.
- **No implicit grants.** Group membership grants nothing on its own;
  roles must be explicitly assigned.
- **No direct user-to-role.** Everything routes through groups.
- **No row-level rules.** ABAC stays in the app; the IAM keeps
  `(user, app, permission)` as its sole answer surface.
- **No app-wide bypass tier.** Just realm-wide and resource-wide.

## Sidebar mirror

The Vue admin shell mirrors the same logic 1:1: each sidebar item
declares the permission it requires, the backend evaluates the same
string. The single source of truth is the permission constant —
frontend gating cannot drift from backend gating because both consult
the identical literal.

```ts
{ section: 'authorization', label: 'nav.users', icon: 'users',
  path: '/admin/users', requirePermissions: ['user:read'] }
```

A user with only `user:read` (in the `modgud` app context) sees just
"Users" in the sidebar — no OAuth, no System.
