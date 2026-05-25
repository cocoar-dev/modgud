# Concepts

## Principals & capabilities

An `IPrincipal` is anything that is addressable by id and can carry
permissions — users, groups, service accounts. The minimal contract:

```csharp
public interface IPrincipal
{
    Guid Id { get; }
    string Type { get; }       // Subclass override: "person" / "group" / "service-account"
    string DisplayName { get; }
    bool IsActive { get; }
    bool IsDeleted { get; }
}
```

`Type` is an abstract getter on `Principal` in the slice; each subtype
overrides it with a stable alias. It is serialised into JSON (not
JsonIgnore) so that Marten LINQ filters `p.Type == "person"` translate
into a JSONB path query.

Specific abilities (**capabilities**) hang off additional interfaces:

```csharp
public interface IPrincipalWithMembers : IPrincipal
{
    IReadOnlyList<Guid> MemberIds { get; }
}

public interface IPrincipalWithAccount : IPrincipal
{
    string AccountName { get; }
}

public interface IPrincipalEmailAddressable : IPrincipal
{
    Task<IReadOnlyList<string>> GetEmailsAsync(IEmailResolutionContext ctx, CancellationToken ct);
}
```

Typical compositions:

| Principal | Members | Account | Email | Note |
|---|---|---|---|---|
| `Person` | — | ✅ | ✅ | Concrete with Firstname/Lastname/Acronym/Email/AccountName/ExternalIdentities |
| `Group` | ✅ | — | ✅ | Shared mailbox or `ExpandToMembers` |
| `ServiceAccount` | — | ✅ | — | Non-human principal, no notifications |

The slice services touch only the interface they need:

- `PermissionService.GetUserGroupsAsync` traverses via
  `IPrincipalWithMembers.MemberIds`
- `PrincipalEmailResolver.ResolveEmailsAsync` calls
  `IPrincipalEmailAddressable.GetEmailsAsync`
- `PermissionEndpointFilter` cares about `IPrincipal.Id` via
  `ClaimTypes.NameIdentifier`

## Roles & permissions

A permission is a fully-qualified string
`<app>:<resource>:<action>` — e.g. `modgud:user:read`,
`modgud:oauth-client:write`, `realm:admin`. A `PermissionRole`
binds a list of actions to a resource type within an app:

```csharp
public class PermissionRole
{
    public string Name { get; set; }              // "User Manager"
    public string AppSlug { get; set; }           // "modgud"
    public string ResourceType { get; set; }      // "user"
    public List<string> Permissions { get; set; } // ["read", "write"]
    //  → modgud:user:read, modgud:user:write
}
```

Permissions flow **only** through groups:

```
User → Group → Role → Permission
```

No direct user → role assignments, no user → permission overrides.
Path: which groups is the user in (transitively, including nested) →
which roles do those groups have → which permissions follow.

### Bypass hierarchy

| String | Effect |
|---|---|
| `<app>:<resource>:admin` | Bypasses all action checks for that resource within that app |
| `<app>:admin` | Bypasses all action checks for all resources within that app |
| `realm:admin` | Bypasses everything in every app (realm-wide emergency exit) |

`hasPermission(needed)` returns true when:

1. the user holds `realm:admin`, or
2. the user holds the requested permission directly, or
3. the user holds `<app>:admin` for the requested permission's app, or
4. the user holds `<app>:<resource>:admin` for the requested
   permission's app + resource

The realm-wide `realm:admin` bypass is intentionally narrow — only
the "System Admin" default role carries it. Typically you give
per-area owners resource-level admin within the IAM app (e.g. "OAuth
owners" get `modgud:oauth-client:admin` +
`modgud:oauth-scope:admin` + `modgud:oauth-api:admin`, but
not `modgud:user:admin`).

## ABAC

Row-level access is deliberately **not** an IAM concern. Modgud
delivers only RBAC answers (`(user, app, permission)`); the question
"may the user see *this* row" depends on the app's own schema and
belongs in the app. See [ABAC and the IAM boundary](/concepts/abac).

## Membership modes

A group is either `Manual` or `Auto`:

- **Manual** — admin maintains `MemberIds` directly
- **Auto** — a membership-script predicate determines the members
  dynamically. On every relevant principal event (create, update,
  delete) the membership is recomputed

The membership script is translated by `Cocoar.JsEval.Linq` into an
`Expression<Func<Principal, bool>>` — a single SQL query against the
principal table returns the new `MemberIds`. For the event-triggered
"did the user change in a way that affects us?" skip, a
dependency collector exists that records which properties each script
reads (`"Firstname"`, `"Email"`). Changes outside of this property
set skip the recompute. Scripts may read only IAM-owned fields — no
app-schema fields.

Nested groups are allowed — an auto-group can have another (manual or
auto) as a member; BFS traversal with a visited set guards against
cycles.

For more detail see [Auto-Membership](./auto-membership).

## Events & projections

All mutations flow as events into the Marten event store:

| Event | When |
|---|---|
| `GroupCreatedEvent` | Create |
| `GroupUpdatedEvent` | Update |
| `GroupMembershipRecomputedEvent` | Auto-membership recomputed successfully |
| `GroupMembershipRecomputeFailedEvent` | Script error — `MembershipLastError` set |
| `GroupDeletedEvent` | Delete |
| `PermissionRoleCreated/Updated/Deleted` | Role CRUD |

Two projections **inline** (synchronously consistent):

1. **`PrincipalProjectionBase`** — abstract; processes all group
   events. The app inherits
   (`Modgud.Authentication.Projections.AuthPrincipalProjection`)
   and adds Apply methods for its person events (`UserCreatedEvent`,
   `UserUpdatedEvent` etc.). The resulting documents land
   polymorphically (via Marten `AddSubClass`) in the
   `mt_doc_principal` table.

2. **`PermissionRoleProjection`** — roles land in their own table.

Inline projections guarantee: *the next query after
`SaveChangesAsync()` sees the state*. For admin UIs (save group →
update dropdown) this is mandatory.

## Polymorphism

All principals land in one table (`mt_doc_principal`). Marten manages
the discriminator (`mt_doc_type`) automatically:

```csharp
martenOpts.Schema.For<Principal>()
    .AddSubClass<Person>("person")
    .AddSubClass<Group>("group")
    .AddSubClass<ServiceAccount>("service-account");
```

The alias `"person"` ends up:

- in `mt_doc_type` (Marten sub-class discriminator, SQL column)
- in JSON under `Type` (from the `Principal.Type` getter — membership
  scripts use `Type.Is(p, 'person')` for polymorphism-safe type
  checks)
- in STJ `$type` (for polymorphic deserialisation of
  `List<Principal>`)

The aliases are independent of the C# class name — a class rename
doesn't break persistence.

## Query patterns

```csharp
// All groups — Marten's SubClass filter ensures only Group rows come back
var groups = await session.Query<Group>()
    .Where(g => !g.IsDeleted)
    .ToListAsync();

// Mixed principals — the polymorphic query returns Person + Group + ServiceAccount
var all = await session.Query<Principal>()
    .Where(p => !p.IsDeleted)
    .ToListAsync();

// Type filter in C#
var onlyGroups = all.OfType<Group>();
var onlyPersons = all.OfType<Person>();
```

`session.Query<TConcrete>()` is filtered at the SQL level
(`WHERE mt_doc_type = 'group'`). `session.Query<Principal>()` scans
the whole table with polymorphic deserialisation. For the BFS in
`PermissionService` the polymorphic scan is fine because all groups
get loaded once anyway.

## Realm scoping

Because the slice operates on the current Marten tenant session, all
principals, roles, and permissions are **automatically realm
isolated**. In realm `acme` you only see Acme groups; in realm
`system` only system groups. That is the consequence of
"database-per-tenant" — the slice doesn't need to know anything about
it.
