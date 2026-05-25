# Auto-Membership

A group is either `Manual` (admin maintains `MemberIds` directly) or
`Auto` (a membership script determines the members dynamically).

## Manual mode

```
Group "Backend Team"
  MembershipMode: Manual
  MemberIds: [<user-1>, <user-2>, <user-3>]
```

Admins add and remove users via the UI. Nothing happens
automatically.

## Auto mode

```
Group "Sales Department"
  MembershipMode: Auto
  MembershipScript: (p) => p.OrganizationalUnit === "sales" && p.IsActive
  MemberIds: [<computed-from-script>]
```

`MemberIds` is maintained by the system, not the admin. On every
relevant event (user created/updated/deleted) the script is
re-evaluated.

## Membership script

A TypeScript arrow function from a principal record to `boolean`:

```typescript
// Predicate form
(p) => p.OrganizationalUnit === "engineering"
    && p.AccountName !== "service-account-bot"
    && p.IsActive

// With externalClaims (from the most recent OIDC login):
(p) => p.externalClaims.department === "Finance"
```

Translated by Cocoar.JsEval.Linq into an
`Expression<Func<Person, bool>>` → SQL against
`mt_doc_principal WHERE mt_doc_type = 'person'`. A single query
returns the new `MemberIds`.

## Recompute triggers

`AutoMembershipSyncHandlers` (Wolverine handlers) listen for person
mutation events:

| Event | Action |
|---|---|
| `UserCreated` | Check auto-groups whose script predicate matches → on match: add user |
| `UserUpdated` | Check auto-groups → add or remove user based on the new state |
| `UserDeleted` | Remove user from all auto-groups |
| `GroupMembershipScriptChanged` | Full recompute pass for that one group |

## Dependency tracking (selective recompute)

Auto-membership recompute is expensive if you do it on every
heartbeat update of the user. Solution: per script, when saving, a
**dependency set** of the read properties is calculated:

```typescript
// Script
(p) => p.OrganizationalUnit === "sales" && p.IsActive

// Dependencies
["OrganizationalUnit", "IsActive"]
```

On `UserUpdated`, we check whether any field in the dependency set
changed. If not → skip the recompute for that group.

Example: a user updates `LastLoginAt`. `IsActive` and
`OrganizationalUnit` are unchanged → the Sales group isn't checked at
all, even though the `UserUpdated` event fired.

## Failure handling

If the script throws (translator error or runtime error during
compile), a `GroupMembershipRecomputeFailedEvent` with the error
message is fired. The Group projection sets `MembershipLastError` and
keeps the previous `MemberIds`. The admin sees the error in the group
detail view.

A successful recompute → `GroupMembershipRecomputedEvent` with the
new `MemberIds`. `MembershipLastError` is set to `null`.

## Nested auto-groups

An auto-group can have another group (manual or auto) as a member:

```
"All Staff" (Manual)
  Members: ["Engineering", "Sales", "Support"]   ← three auto-groups

"Engineering" (Auto)
  Script: (p) => p.OrganizationalUnit === "engineering"

"Sales" (Auto)
  Script: (p) => p.OrganizationalUnit === "sales"
```

The permission BFS expands this without special-casing —
`IPrincipalWithMembers` is polymorphic. Cycle detection via a visited
set.

## Initial recompute

When an admin creates a new auto-group (or changes the script), an
initial full pass runs:

```csharp
// IAutoMembershipRecalculator
await recalculator.RecomputeAllMembersAsync(group, ct);
```

→ a single SQL query against all person documents, with the script as
a WHERE clause. Result → set `MemberIds` + fire event.

At a million persons this could be slow — but modgud is
currently sized for an order of magnitude well below that
(mid-sized SaaS org charts, a few thousand users per realm).

## Example setup

```
Group "OU Sales" (Auto)
  Script: (p) => p.OrganizationalUnit === "sales" && p.IsActive
  Roles: ["Sales Read", "Customer Manager"]

Group "Active Engineers" (Auto)
  Script: (p) => p.Department === "engineering"
              && p.IsActive
              && !p.AccountName.startsWith("svc-")
  Roles: ["Code Repo Reader", "CI Trigger"]
```

When a new sales user is provisioned via OIDC login:

1. `UserCreated` with `OrganizationalUnit=sales` fires
2. `AutoMembershipSyncHandlers` evaluates both auto-scripts:
   - "OU Sales" matches → user is added to membership
   - "Active Engineers" doesn't match → no effect
3. `GroupMembershipRecomputedEvent` fires for "OU Sales"
4. SignalR notification to all admin browsers → the group list in the
   frontend updates automatically (via `useEntityService`
   subscriptions)
5. The user automatically inherits all permissions of "OU Sales" →
   can see customer data immediately, without anyone clicking
   anything
