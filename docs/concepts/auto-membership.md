# Auto-Membership

A group is either `Manual` (an admin maintains `MemberIds` directly) or `Auto` (a membership script decides the members dynamically). Orthogonally, an `Auto` group may also be marked **`ExternallyDrivable`**, which lets a federated login confer membership *for that session only* — see [Externally-driven membership](#externally-driven-membership-federation) below.

## Manual mode

```
Group "Backend Team"
  MembershipMode: Manual
  MemberIds: [<user-1>, <user-2>, <user-3>]
```

Admins add and remove members via the UI. Nothing happens automatically.

## Auto mode

```
Group "Active Staff"
  MembershipMode: Auto
  MembershipScript: (p) => Type.Is(p, 'person') && p.IsActive
  MemberIds: [<computed-from-script>]
```

`MemberIds` is maintained by the system, not the admin. On every relevant event (user created / updated / deleted) the script is re-evaluated.

## Membership script

A TypeScript arrow function from a principal record to `boolean`. It is evaluated against each candidate principal; returning `true` means "is a member".

```typescript
(p) => Type.Is(p, 'person')
    && p.IsActive
    && p.Email != null
    && p.Email.endsWith('@acme.com')
    && p.AccountName !== 'svc-bot'
```

`Type.Is(p, 'person')` is the type guard — it narrows `p` to a person principal (the same predicate works on groups and service accounts, so guard first). The fields a script can read on a person are the persisted `Person` columns:

| Field | Type | Notes |
|---|---|---|
| `p.IsActive` / `p.IsDeleted` | `boolean` | lifecycle flags |
| `p.AccountName` | `string?` | login name |
| `p.Firstname` / `p.Lastname` | `string?` | display name parts |
| `p.Acronym` | `string?` | short initials |
| `p.Email` | `string?` | primary email |
| `p.NormalizedUserName` / `p.NormalizedEmail` | `string?` | upper-cased, for case-insensitive compares |
| `p.ExternalIdentities` | `{ LinkId, LoginProviderId, Issuer }[]` | the IdP links the user holds — query with `.some(...)`, see below |

> These are the **only** durable fields. There is no `OrganizationalUnit`, `Department`, or generic `externalClaims` dictionary on a person — scripts that reference them either fail to transpile or silently never match. To drive membership from an upstream IdP's groups, use an `ExternallyDrivable` group and `p.ExternalGroups` (below).

### Reading `p.ExternalIdentities` (durable IdP links)

`p.ExternalIdentities` is the durable list of external-identity links on the person — one entry per linked IdP, each `{ LinkId, LoginProviderId, Issuer }`. Unlike `p.ExternalGroups` (the *ephemeral, session-only* group surface for `ExternallyDrivable` groups), `ExternalIdentities` is a **persisted** field, so a normal `Auto` group (not `ExternallyDrivable`) can key on it — e.g. "everyone federated through a given IdP":

```typescript
// "Member if linked to the Entra tenant IdP."
(p) => Type.Is(p, 'person')
    && p.ExternalIdentities.some(x => x.Issuer === 'https://login.microsoftonline.com/<tenant>/v2.0')
```

> **Use `.some(x => ...)`, not `.length`.** Membership on a sub-collection must be expressed as `.some(predicate)` — that is what translates to SQL in the durable batch engine. A count form like `p.ExternalIdentities.length > 0` does **not** translate and silently never matches in the batch engine, so avoid it. The membership-script editor's IntelliSense exposes the element fields (`LinkId` / `LoginProviderId` / `Issuer`).

Linking or unlinking an external identity re-evaluates `p.ExternalIdentities` scripts (see [Recompute triggers](#recompute-triggers-durable-groups)).

The membership-script editor's IntelliSense is generated from the real CLR types, so the available members are always in sync — use it to discover the surface.

### How it runs (the batch engine)

The membership script is translated into a single database query that
matches every person record against the predicate in one pass, and the
result becomes the new `MemberIds`. This is the durable path: it writes
`MemberIds` and only ever sees the persisted `Person` fields (it cannot
see the ephemeral federation surface, which isn't stored).

## Externally-driven membership (federation)

An `Auto` group can additionally be marked **`ExternallyDrivable`**. Such a group is **skipped by the batch engine** (it never writes durable `MemberIds`) and is instead evaluated **in memory, at login time**, by the federation deriver — but only when the login arrives through a provider the realm admin marked `TrustForAuthorization`. The match is **session-scoped**: it lives on the sign-in only, is unioned into the access decision while the session/grant is valid, and disappears when the session ends. The session is the lease — nothing is persisted, and the upstream group names never leave Modgud (they are expanded into Modgud roles/permissions before any token or UserInfo response, the hub boundary).

On top of the durable `Person` fields, an `ExternallyDrivable` script may read the ephemeral federation surface:

| Field | Type | Notes |
|---|---|---|
| `p.ExternalGroups` | `string[]` | the current provider's `groups` claim for this login (always an array — use `.includes(...)`) |
| `p.Source` | `string` | the source tag of this login: `"local"` or `"provider:<slug>"` |

```typescript
// "Place this login into the group if the upstream IdP put them in 'entra-admins'."
(p) => Type.Is(p, 'person')
    && p.IsActive
    && p.ExternalGroups.includes('entra-admins')

// Scope a rule to one provider via p.Source (v1 has no declarative per-provider
// binding yet — the script scopes itself):
(p) => p.Source === 'provider:acme-entra'
    && p.ExternalGroups.includes('finance')
```

Key rules:

- **Live-only.** `p.ExternalGroups` reflects *this* login's provider only — a password (local) login carries an empty array, so it never picks up a previously-seen IdP's groups (no stale-admin trap).
- **Never durable.** A match here is never written to `MemberIds`; it exists only for the session.
- **`realm:admin` is local-only.** A group whose roles confer `realm:admin` **cannot** be marked `ExternallyDrivable` (the editor blocks it and the API rejects it), and even an inherited ancestor that confers `realm:admin` is stripped from a session-sourced grant. Manage realm-admin membership manually. `<app>:admin` and below may be externally driven.

## Recompute triggers (durable groups)

The durable engine listens for person-mutation events and re-evaluates the affected `Auto` (non-`ExternallyDrivable`) groups:

| Event | Action |
|---|---|
| user created | check auto-groups whose predicate matches → on match: add |
| user updated | re-check auto-groups → add or remove based on the new state |
| user deleted | remove from all auto-groups |
| external identity linked / unlinked | re-check auto-groups (e.g. `p.ExternalIdentities` scripts) → add or remove |
| membership script changed | full recompute pass for that one group |

## Dependency tracking (selective recompute)

Recomputing every auto-group on every heartbeat update would be wasteful. So per script, the **set of read properties** is recorded when it is saved:

```typescript
// Script
(p) => p.IsActive && p.Email != null && p.Email.endsWith('@acme.com')

// Dependencies
["IsActive", "Email"]
```

On a user update, only groups whose dependency set intersects the changed fields are re-checked. Example: a user updates `LastLoginAt` (not a person field a script reads) → `IsActive`/`Email` unchanged → the group above is not re-evaluated even though the update event fired.

## Failure handling

If the script throws (a translator error, or a runtime error during compile), the recompute fails closed: the Group projection records the error in `MembershipLastError` and keeps the previous `MemberIds`. The admin sees the error in the group detail view. A successful recompute clears `MembershipLastError` and writes the new `MemberIds`.

## Nested auto-groups

An auto-group can have another group (manual or auto) as a member:

```
"All Staff" (Manual)
  Members: ["Engineering", "Sales", "Support"]   ← three auto-groups

"Engineering" (Auto)
  Script: (p) => Type.Is(p, 'person') && p.AccountName != null && p.AccountName.startsWith('eng-')
```

The permission BFS expands this without special-casing — `IPrincipalWithMembers` is polymorphic, with cycle detection via a visited set. Session-derived membership inherits the same way: a session-matched child group still confers its parent groups' roles for that session.

## Initial recompute

When an admin creates a new auto-group (or changes the script), an initial full pass runs — a single query matches the script against every person record, sets `MemberIds`, and fires the recompute event. Modgud is sized for mid-sized org charts (a few thousand users per realm), where this is sub-second; it is not built for million-row tenants.

## Example setup

```
Group "Active Engineers" (Auto)
  Script: (p) => Type.Is(p, 'person')
              && p.IsActive
              && p.AccountName != null
              && p.AccountName.startsWith('eng-')
  Roles: ["Code Repo Reader", "CI Trigger"]

Group "Entra Admins" (Auto, ExternallyDrivable)
  Script: (p) => Type.Is(p, 'person') && p.ExternalGroups.includes('entra-admins')
  Roles: ["Tenant Operator"]
```

When a new engineer is provisioned (a person with `AccountName` `eng-…` is created):

1. The create event fires.
2. The durable engine evaluates the non-drivable auto-scripts: "Active Engineers" matches → the user is added to `MemberIds`; a recompute event fires.
3. SignalR pushes the change to admin browsers → the group list updates live (via `useEntityService` subscriptions).
4. The user inherits "Active Engineers"' permissions immediately.

When that same user later signs in through the trusted EntraID provider and the assertion carries `groups: ["entra-admins"]`:

1. The federation deriver evaluates the `ExternallyDrivable` "Entra Admins" script in memory → match.
2. "Tenant Operator" is unioned into **this session's** access — no `MemberIds` write.
3. The grant carries it for the session's lifetime; the next password login (or a login through an untrusted provider) carries it no more.
