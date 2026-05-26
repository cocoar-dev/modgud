# Modgud.Authorization — slice blueprint

How the authorization slice is built. This page is pure
implementation reference — for the user-facing story see the public
docs (`/concepts/permissions`, `/concepts/auto-membership`,
`/concepts/abac`, `/concepts/groups-and-authorization`).

## Project layout

Standalone C# project at `src/dotnet/Modgud.Authorization/`, copied
from TimeToDo's slice of the same name and extended with IdP-specific
resources. Consumed by `Modgud.Api` via `ProjectReference`. Wires up
through:

| Entry point | Where called | What it does |
|---|---|---|
| `services.AddModgudAuthorization(opts)` | `Program.cs` | DI registrations + resource registry |
| `martenOpts.UseModgudAuthorization()` | Marten configure callback | sub-class mapping, inline projections |

The slice owns the runtime side of authorization — evaluator,
endpoint filter, projection — but **no HTTP endpoints**. CRUD is
done by the calling app dispatching Wolverine commands
(`IMessageBus.InvokeAsync<ErrorOr<Group>>(command)`). The slice
deliberately stops at:

- No login methods, 2FA, OIDC (those belong in `Modgud.Authentication`)
- No HTTP CRUD endpoints (the app defines its endpoints)
- No realm/tenant routing (the slice operates on the current Marten
  tenant session; `RealmMiddleware` in the Api layer sets it)

## Dependencies

| Hard | Reason |
|---|---|
| Marten 9+ | Event store + polymorphic document storage (sub-class mapping) |
| WolverineFx.Marten | Commands + handler discovery + outbox |
| Cocoar.JsEval + .Linq + .TypeScript | TS → JS transpile + JS → expression-tree translation for membership scripts |
| ErrorOr | Command return types |
| Microsoft.AspNetCore.App | `IEndpointFilter` for `RequiresPermission` |

## IPrincipal + capability interfaces

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

`Type` is an abstract getter on `Principal`; each subtype overrides
it with a stable alias. It is serialised into JSON (not
`[JsonIgnore]`) so that Marten LINQ filters
`session.Query<Principal>().Where(p => p.Type == "person")` translate
into a JSONB path query rather than requiring a polymorphic load
into memory.

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

| Principal | Members | Account | Email | Notes |
|---|---|---|---|---|
| `Person` | — | ✅ | ✅ | Concrete: Firstname/Lastname/Acronym/Email/AccountName/ExternalIdentities |
| `Group` | ✅ | — | ✅ | Shared mailbox or `ExpandToMembers` |
| `ServiceAccount` | — | ✅ | — | Non-human principal, no notifications |

Slice services only touch the interface they need:

- `PermissionService.GetUserGroupsAsync` traverses
  `IPrincipalWithMembers.MemberIds`
- `PrincipalEmailResolver.ResolveEmailsAsync` calls
  `IPrincipalEmailAddressable.GetEmailsAsync`
- `PermissionEndpointFilter` cares about `IPrincipal.Id` via
  `ClaimTypes.NameIdentifier`

Adding a new capability ⇒ define a new interface + adjust the
projection that materialises it. The base `Principal` polymorphic
table stays untouched.

::: tip Sub-class registration
A new `Principal` subclass needs **two** registrations: Marten
`AddSubClass<>` (for storage) and STJ `JsonDerivedType` (for
polymorphic deserialisation). Forgetting either lands the documents
in `mt_doc_<typename>` instead of `mt_doc_principal` and silent
cross-type queries miss them. See the
`feedback_principal_subclass_marten` memory.
:::

## Polymorphism (Marten sub-class mapping)

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
  scripts use `Type.Is(p, 'person')` for polymorphism-safe type checks)
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
`PermissionService` the polymorphic scan is fine because every group
gets loaded once anyway.

## Inline projections

Two projections run **inline** (synchronously consistent with
`SaveChangesAsync`):

1. **`PrincipalProjectionBase`** — abstract. Handles every group
   event (`GroupCreatedEvent`, `GroupUpdatedEvent`,
   `GroupMembershipRecomputedEvent`,
   `GroupMembershipRecomputeFailedEvent`, `GroupDeletedEvent`). The
   consuming app inherits and adds Apply methods for its person
   events. In Modgud,
   `Modgud.Authentication.Projections.AuthPrincipalProjection` is the
   concrete subclass; it handles `UserCreatedEvent`,
   `UserUpdatedEvent`, `UserGdprErasedEvent`, etc.
2. **`PermissionRoleProjection`** — roles land in their own table.

Inline guarantees: *the next query after `SaveChangesAsync()` sees
the new state*. For admin UIs (save group → instantly available in
the dropdown) this is mandatory.

::: tip Marten 9 source-gen for live aggregates too
Under Marten 9, **every** aggregate type loaded via
`session.Events.AggregateStreamAsync<T>` gets a synthetic
`SingleStreamProjection<T,Guid>` wrapper that needs the source-generated
dispatcher — not just classes with the `Projection` suffix. Declare
the class `partial` and reference the Marten source-gen analyzer.
See the `feedback_marten9_sourcegen_aggregates` memory.
:::

## ResourceRegistry

The central hub for permission strings. The slice provides
`IResourceRegistry`; each app registers its resources at boot:

```csharp
services.AddModgudAuthorization(opts =>
{
    opts.RegisterResource("user");
    opts.RegisterResource("permission-role");
    opts.RegisterResource("authorization-group");
    opts.RegisterResource("oauth-client");
    opts.RegisterResource("oauth-scope");
    opts.RegisterResource("oauth-api");
    opts.RegisterResource("login-provider");
    opts.RegisterResource("idp-config");
    opts.RegisterResource("realm");
    opts.RegisterResource("auth-log");
    opts.RegisterResource("app");
    opts.RegisterResource("asset");
    opts.RegisterResource("observability");
    opts.RegisterResource("realm-settings");
    opts.RegisterResource("service-account");
});
```

Per resource the standard actions `read`, `write`, `delete`, `admin`
are available. The admin UI shows the catalog in the role editor; the
endpoint filter checks strings on `RequiresPermission`.

The runtime catalog is the **source of truth**; the catalog seeder
(`PermissionCatalogSeeder`) is **evolving** — it diffs registered
resources against the catalog on every boot and appends missing
permissions per realm. Drift on existing realms is fixed at startup
without manual migration. See the
`project_permission_catalog_drift_fix_2026_05_24` memory for the
incident that drove this.

## PermissionRole & evaluator

A `PermissionRole` references catalog entries via `PermissionIds`,
scoped to an `AppId`:

```csharp
public class PermissionRole
{
    public string Name { get; set; }              // "User Manager"
    public Guid AppId { get; set; }               // the modgud App
    public List<Guid> PermissionIds { get; set; } // refs into App.Permissions
    public bool IsRealmAdmin { get; set; }        // realm-wide bypass when true
}
```

Permissions flow only through groups:

```
User → Group → Role → Permission
```

No direct user→role assignment, no user→permission overrides.

The evaluator is pure (`PermissionEvaluator` in
`Modgud.Permissions.Abstractions`) and reused on both ends of the
wire — IdP-side and resource-server-side (via
`Modgud.Client.AspNetCore`). It implements only two bypass tiers:

```
Evaluate(grants, "user:read") =
   grants.contains("realm:admin")
|| grants.contains("user:read")
|| grants.contains("user:admin")
```

The `realm:admin` literal is a *realm-constant* — never a catalog
entry; the resolver synthesises it for any role with
`IsRealmAdmin = true`.

## Endpoint filter

`RequiresPermission` is an extension on `RouteHandlerBuilder`:

```csharp
app.MapGet("/api/admin/users", ListUsers)
   .RequiresPermission("user:read");
```

Behind it is `PermissionEndpointFilter` (an `IEndpointFilter`).
Resolution path:

1. `ClaimTypes.NameIdentifier` → user id
2. `IPermissionResolver.GetGrantsAsync(userId)` →
   `IReadOnlySet<string>` of resolved permission strings
3. `PermissionEvaluator.Evaluate(grants, "user:read")` → 200 or 403

The default `IPermissionResolver` caches per-request (`HttpContext.Items`)
so multiple `RequiresPermission` filters on the same request hit one
DB read.

## Auto-Membership

`Group.Membership` is either `Manual` or `Auto`. Auto-groups carry a
membership script — a TypeScript predicate that returns `boolean`
for a `Principal`.

```
TypeScript source
   │
   ▼ Cocoar.JsEval.TypeScript
JavaScript (ESM-ish, narrowed surface)
   │
   ▼ Cocoar.JsEval.Linq
Expression<Func<Principal, bool>>
   │
   ▼ Marten LINQ
Single SQL query → MemberIds
```

The translator runs against a **whitelisted** AST: type checks via
`Type.Is(p, 'person')`, property reads against IAM-owned fields only
(`DisplayName`, `Email`, `IsActive`, `ExternalIdentities`). Arbitrary
method calls, regex, or app-schema fields are rejected at translate
time — not at runtime, so the membership script cannot reach app
schema even by accident. App-schema-aware predicates belong in the
consuming app's ABAC layer (see `/concepts/abac`).

### Selective recompute

`MembershipDependencyCollector` records which properties a script
reads (`"Firstname"`, `"Email"`, …) the first time it is translated.
On every relevant principal event (`UserUpdatedEvent`, etc.) the
projection checks: did any of *those* properties change? If not,
skip the recompute. Cuts unnecessary recomputes by ~one to two orders
of magnitude on realms with many auto-groups.

### Nested groups

An auto-group can have another (manual or auto) group as a member.
BFS traversal with a visited set guards against cycles. The BFS lives
in `PermissionService.GetUserGroupsAsync` and uses
`session.Query<Principal>()` once at start (polymorphic load), then
walks the resulting graph in memory.

### Failure mode

Script errors land as `GroupMembershipRecomputeFailedEvent`; the
projection writes `MembershipLastError` on the read model so the
admin UI surfaces the error. Members are *not* cleared — the previous
membership stays until the script is fixed.

## Input limits

`ScriptInputLimits` caps membership script size (16 KiB) and AST
depth (50) before either the JsEval engine or the LINQ translator
sees the input. Belt-and-braces on top of JsEval 4.0's own depth
caps. Driven by the JsEval-fuzzing initiative (`project_jseval_fuzzing`
memory).

## Bootstrap roles

When the first admin in a realm is created (recovery CLI or HTTP
bootstrap), `RealmAdminBootstrapper` atomically seeds three default
`PermissionRole`s and places the new admin into the **Administratoren**
group:

| Role | Permissions (within the `modgud` app catalog unless noted) |
|---|---|
| **System Admin** | `IsRealmAdmin = true` → realm-wide bypass; the group's `BoundTo = ["*"]` |
| **User Manager** | `user:read`, `user:write`, `permission-role:read`, `authorization-group:read`, `authorization-group:write` |
| **Viewer** | `user:read`, `permission-role:read`, `authorization-group:read`, `oauth-client:read`, `oauth-scope:read` |

## Realm scoping

Because the slice operates on the current Marten tenant session, all
principals, roles, and permissions are **automatically realm
isolated**. In realm `acme` you only see Acme groups; in realm
`system` only system groups. That's the consequence of
"database-per-tenant" — the slice itself doesn't have any realm-aware
code.
