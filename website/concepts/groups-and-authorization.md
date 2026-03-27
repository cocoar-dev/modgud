# Authorization Model

## Overview

Cocoar.Auth implements a flexible authorization model based on **Grants** — assignments that connect users or groups to roles, optionally scoped to specific API resources. The model scales from simple single-role setups to complex enterprise structures with nested groups and resource-scoped permissions.

Every authorization decision follows one principle: **the token contains only what was requested and what the user is allowed to access.**

## Core Concepts

### OAuth Scope

The smallest unit of authorization. A scope describes a single action on an API resource.

- Defined on API resources by the developer
- Examples: `billing.read`, `billing.write`, `repo.delete`
- Scopes appear in OAuth tokens — consumer apps use them to make access decisions
- Scopes are never assigned directly to users, they are bundled in roles

### API Resource

A protected service or API endpoint.

- Examples: "Billing API", "Code Repository", "Dashboard API"
- Each API resource defines the set of scopes it supports
- API resources serve as the **scope boundary** for grants

### Role

A named bundle of scopes. Roles describe **capabilities** — what someone can do.

- Example: "Billing Manager" = `billing.read` + `billing.write` + `billing.export`
- Roles are admin-configurable — new roles can be created from any combination of existing scopes
- Roles can be **realm roles** (global) or **client roles** (scoped to a specific OAuth client)

### Group

An organizational unit that groups users together. Groups describe **belonging** — who works together.

- Examples: "Backend Team", "Finance Department", "Team Leads"
- Groups can be **nested** — a group can contain other groups as children
- A user can be a member of multiple groups
- Groups are **optional** — direct role assignments work without groups

### Grant

The assignment that connects everything. A grant has three dimensions:

```
Grant = {
  Subject:   User or Group          (who)
  Role:      Role with scopes       (can do what)
  Scope:     API Resource/Client    (where — optional)
}
```

When the scope is omitted, the grant applies globally (realm role). When scoped, the role and its scopes apply only to that specific API resource or OAuth client.

## How It All Fits Together

```
User ──► Grant ──► Role ──► OAuth Scopes
  │         │
  │      Scope (API Resource / Client)
  │
  └──► Group ──► Grant ──► Role ──► OAuth Scopes
                    │
                 Scope (API Resource / Client)
```

A user's effective scopes are the **union** of all grants — from direct assignments and from all groups they belong to (including nested group inheritance).

### Example Setup

```
API Resource: "Billing API"
  └── Scopes: billing.read, billing.write, billing.export

API Resource: "Dashboard API"
  └── Scopes: dash.read, dash.configure

Role: "Billing Manager"  → billing.read, billing.write, billing.export
Role: "Billing Viewer"   → billing.read
Role: "Dashboard Admin"  → dash.read, dash.configure

Group: "Finance Team"
  └── Grant: Role "Billing Manager" scoped to "Billing API"
  └── Members: alice, bob

User "charlie"
  └── Direct Grant: Role "Billing Viewer" scoped to "Billing API"
```

### Token Issuance — Filtered by Request

When an OAuth client requests a token, the response contains **only what was requested and what the user is allowed to access:**

```
Client requests:  scope=billing.read billing.write

User "alice" has:
  ├── "Billing Manager" on "Billing API"   → billing.read, billing.write, billing.export
  └── "Dashboard Admin" on "Dashboard API" → dash.read, dash.configure

Token contains:
{
  "scope": "billing.read billing.write",
  "realm_access": { "roles": [] },
  "resource_access": {
    "billing-api": { "roles": ["Billing Manager"] }
  }
}
```

- `billing.export` is not in the token — not requested by the client
- Dashboard roles are not in the token — no dashboard scopes were requested
- The consumer app sees only what it needs — no information leakage about other APIs

**Token filtering formula:** `requested scopes ∩ user's effective scopes = granted scopes`

## Scaling Model

The model grows with your needs. All stages coexist — you can use all three simultaneously.

### Simple — realm roles, no groups

For small applications with few users. No groups needed, no API scoping needed.

```
User "admin"  → Realm Role "Admin"
User "viewer" → Realm Role "User"
```

Token: `realm_access.roles: ["Admin"]`
Consumer: `[Authorize(Roles = "Admin")]`

### Medium — scoped roles per API

When multiple APIs connect and need independent role namespaces.

```
User "alice"
  ├── Role "Editor" scoped to "Billing API"
  └── Role "Viewer" scoped to "Dashboard API"
```

Token: `resource_access.billing-api.roles: ["Editor"]`

### Large — groups with scoped roles

When managing individual role assignments becomes impractical.

```
Group "Finance Team"
  ├── Grant: "Billing Manager" on "Billing API"
  ├── Grant: "Viewer" on "Dashboard API"
  └── Members: alice, bob, charlie, ... (30 users)

Group "Engineering"
  ├── Backend Team
  │     ├── Grant: "Editor" on "Code API"
  │     └── Members: dave, eve
  └── Frontend Team
        ├── Grant: "Editor" on "Dashboard API"
        └── Members: frank, grace
```

New team member? Add to group — they get all the right scopes automatically.

At every stage, the token looks the same to the consumer — roles grouped by client, scopes filtered by request. The consumer never needs to know how a role was assigned.

## Nested Groups

Groups can contain other groups as children. Grants inherit downward — members of child groups inherit all grants from parent groups.

```
Engineering                          → Grant: "Viewer" on "Code API"
  ├── Backend Team                   → Grant: "Editor" on "Code API"
  │     └── User "alice"
  └── Frontend Team                  → Grant: "Editor" on "Dashboard API"
        └── User "bob"
```

- **alice** gets: Viewer + Editor on Code API (from Engineering + Backend Team)
- **bob** gets: Viewer on Code API (from Engineering) + Editor on Dashboard API (from Frontend Team)

Nesting is recursive — no depth limit. Cycles are prevented by validation.

## Groups in Tokens

Groups are **not included in tokens by default**. Consumer apps authorize based on roles and scopes, not group membership.

If needed (e.g. "show all members of my team"), group claims can be enabled per client:

```json
{
  "groups": ["/Engineering/Backend Team"]
}
```

## Roles with Scopes

Roles bundle scopes and are admin-configurable. The admin creates roles from the scopes defined on API resources:

```
Available scopes (from API resources):
  billing.read, billing.write, billing.export, dash.read, dash.configure

Admin creates:
  "Billing Manager"  = billing.read + billing.write + billing.export
  "Dashboard Viewer"  = dash.read
  "Full Access"       = billing.read + billing.write + billing.export + dash.read + dash.configure
```

New scopes appear automatically when a developer adds them to an API resource. The admin can then include them in roles.

### Realm Roles vs Client Roles

| | Realm Role | Client Role |
|---|---|---|
| **Scope** | Global — all APIs | Scoped to one OAuth client |
| **In token** | `realm_access.roles` | `resource_access.{client}.roles` |
| **Use case** | Platform-wide ("Admin") | Per-app ("Billing Manager") |

## Event Sourcing

### Group Aggregate

**Lifecycle**
- `GroupCreated` — name, description
- `GroupRenamed` — name or description changed
- `GroupArchived` — soft-deleted

**Membership**
- `MemberAdded` — user added
- `MemberRemoved` — user removed

**Nesting**
- `ChildGroupAdded` — child group added
- `ChildGroupRemoved` — child group removed

**Role Grants**
- `RealmRoleGranted` — realm role assigned to group
- `RealmRoleRevoked` — realm role removed
- `ClientRoleGranted` — client role assigned with API resource scope
- `ClientRoleRevoked` — client role removed

### Role Aggregate (existing)

- `RoleCreated` — with optional ClientId (realm vs client role)
- `RoleScopesChanged` — scopes added or removed from role
- `RoleNameChanged`, `RoleDeleted`, etc.

## Projections

| Projection | Type | Purpose |
|-----------|------|---------|
| `GroupState` | Inline | Command validation: members, children, grants |
| `GroupListReadModel` | Async | Admin grid: name, member count |
| `GroupDetailReadModel` | Async | Admin detail: denormalized members, children, grants |
| `UserEffectiveRoles` | Async | Pre-computed roles per user per client — used at token issuance |

## Validation Rules

- A user cannot be added to the same group twice
- The same grant (same role + same scope) cannot exist twice on a group
- An archived group does not accept new members or grants
- Nested group cycles are not allowed
- Role and API resource references must point to existing entities
- Scopes assigned to a role must be valid scopes on the role's API resource

## What This System Is NOT

- **Not ABAC** — no attribute-based policies ("if time > 18:00"). Role-based with scoping.
- **No deny grants** — only positive grants. Effective access is always the union. Simplicity over flexibility.
- **No implicit permissions** — group membership does not automatically grant access. Roles must be explicitly assigned.
- **The IDP does not enforce access** — it delivers roles and scopes in the token. The consumer app makes the authorization decision.
