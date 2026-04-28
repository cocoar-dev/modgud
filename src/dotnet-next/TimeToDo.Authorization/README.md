# TimeToDo.Authorization

A **vertical slice** owning everything authorization-related in TimeToDo:
principals (Person + Group), permissions, role-based grants, group-membership
resolution (incl. auto-membership via JsEval-scripted predicates), and
resource-specific access policies (also JsEval-scripted).

> **This is not a reusable library.** It is a self-contained reference
> implementation — designed for **copy + adapt** when the same authorization
> shape is needed in another app. No abstract extension points, no
> generic hooks, no API stability promises across apps. Adopting apps own
> their copy and modify it freely.

---

## Why a separate `csproj` if it's not a library?

Three reasons that have nothing to do with reusability:

1. **Boundary enforcement.** The compiler stops anyone in `TimeToDo.Api` from
   reaching into authorization internals — only what's `public` is visible.
2. **Explicit dependency direction.** `TimeToDo.Domain → TimeToDo.Authorization`
   is recorded in csproj references. Reversed accidents are caught at build time.
3. **Adoption clarity.** When pointing an AI agent at "the authorization slice
   from TimeToDo," the answer is one folder + one csproj. No file-hunting across
   `Domain/Identity`, `Infrastructure/Identity`, `Api/Features/Groups`, etc.

---

## Public surface (what consumers call)

```
TimeToDo.Authorization.Setup.ServiceCollectionExtensions
   .AddTimeTodoAuthorization(opts => opts.RegisterResource(...))

TimeToDo.Authorization.Setup.MartenStoreOptionsExtensions
   .UseTimeTodoAuthorization()                        // on StoreOptions
   .AddTimeTodoAuthorizationPolymorphism()            // on JsonSerializerOptions

TimeToDo.Authorization.AspNetCore.PermissionEndpointFilter
   .RequiresPermission("resource:action")             // RouteHandlerBuilder ext

TimeToDo.Authorization.Services
   IPermissionService                                  // BFS Principal → Group → Role
   IPrincipalLookupService                             // by-id + lightweight lookup
   IPrincipalEmailResolver                             // shared / expand-to-members
   IAccessPolicyEngine                                 // resource access scripts
   IMembershipEvaluator                                // auto-membership scripts
   IAutoMembershipRecalculator                         // recompute on principal change

TimeToDo.Authorization.Commands
   CreateGroupCommand / CreateGroupHandler             // Wolverine
   UpdateGroupCommand / UpdateGroupHandler

TimeToDo.Authorization.Events
   GroupCreatedEvent / GroupUpdatedEvent / GroupDeletedEvent
   GroupMembershipRecomputedEvent / GroupMembershipRecomputeFailedEvent
   PermissionRoleCreatedEvent / PermissionRoleUpdatedEvent / PermissionRoleDeletedEvent

TimeToDo.Authorization.Principals
   Principal (abstract) → Person, Group, ServiceAccount (all concrete)
   IPrincipal, IPrincipalWithAccount, IPrincipalWithMembers, IPrincipalEmailAddressable
   ExternalIdentityRef
   MembershipMode (Manual/Auto), EmailMode (Shared/ExpandToMembers)

TimeToDo.Authorization.Roles
   PermissionRole                                      // doc

TimeToDo.Authorization.Resources
   IResourceRegistry, ResourceRegistry                 // declares "todo: read,create,..."

TimeToDo.Authorization.Projections
   PrincipalProjectionBase                             // group events → mt_doc_principal
   PermissionRoleProjection                            // role events → mt_doc_permissionrole
```

---

## Architecture in one diagram

```
                     ┌────────────────────────────────┐
                     │  Principal (abstract)          │
                     │   Id, IsActive, IsDeleted,     │
                     │   DisplayName                  │
                     └──────┬───────┬──────┬──────────┘
                            │       │      │
              ┌─────────────┘       │      └──────────────┐
              │                     │                     │
        ┌─────▼─────┐         ┌─────▼─────┐         ┌─────▼─────────┐
        │  Person   │         │   Group   │         │ ServiceAccount│
        │  (concr.) │         │  (concr.) │         │   (concr.)    │
        └───────────┘         └─────┬─────┘         └───────────────┘
                                    │
                              MemberIds, RoleIds,
                              AccessScripts,
                              Membership (Manual/Auto + script),
                              Email (Shared/ExpandToMembers)

  Principal → Group → PermissionRole → Permissions ("resource:action")
                  ▲ BFS over MemberIds (transitive groups)

  Auto-membership: Group's TypeScript predicate
    (p) => Type.Is(p, 'person') && p.Firstname.startsWith('A')
  → JsEval transpiles to LINQ → Marten emits SQL filter on mt_doc_principal
```

**Storage shape:**
- `mt_doc_principal` — polymorphic table, holds Person + Group docs,
  distinguished by Marten's `mt_doc_type` discriminator (`"person"` / `"group"`)
- `mt_doc_permissionrole` — flat doc table for roles
- `mt_events` — event store, all auth events use stable snake_case aliases:
  `authorization_group_*`, `permission_role_*`

---

## How TimeToDo wires it (look here when copying)

`src/dotnet/TimeToDo.Infrastructure/DependencyInjection.cs` :: `AddInfrastructure`

```csharp
services.AddTimeTodoAuthorization(opt =>
{
    opt.RegisterResource("todo", "read", "create", "update", "delete", ...);
    opt.RegisterResource("customer", "read", "create", "update", ...);
    opt.RegisterResource("comment", "read", "create", "delete");
    opt.RegisterResource("app", "admin");
});
```

`src/dotnet/TimeToDo.Infrastructure/Persistence/Marten/Configuration/MartenConfiguration.cs`

```csharp
options.UseSystemTextJsonForSerialization(EnumStorage.AsString, configure: o => {
    o.AddOptionalAware();
    o.AddTimeTodoAuthorizationPolymorphism();   // ← STJ $type for Principal
});
options.UseTimeTodoAuthorization();             // ← sub-class mapping + event aliases
```

That's the entire wiring footprint. Person events (`UserCreatedEvent` etc.) are
TimeToDo-specific (Identity adapter emits them) and live in
`TimeToDo.Domain.Users.Events` + `TimeToDo.Domain.Identity.Events`. The Person
side of the principal projection (`TimeToDoPrincipalProjection` in
`TimeToDo.Infrastructure/.../Projections/Principals/`) bridges those to the
auth slice's `Principal` table by extending `PrincipalProjectionBase`.

---

## Adoption guide — copying this into another app

Goal: stand up the same auth model in a new app, adapted to its needs.

### Step 1 — Copy the project

```bash
cp -r src/dotnet/TimeToDo.Authorization YourApp/YourApp.Authorization
```

Rename:
- Folder name → `YourApp.Authorization`
- `TimeToDo.Authorization.csproj` → `YourApp.Authorization.csproj`
- Inside csproj: `<RootNamespace>` and `<AssemblyName>` to `YourApp.Authorization`
- All `namespace TimeToDo.Authorization.*` → `namespace YourApp.Authorization.*`
- All `using TimeToDo.Authorization.*` in your app code → `using YourApp.Authorization.*`
- Extension method names if you want: `AddTimeTodoAuthorization` → `AddYourAppAuthorization`,
  `UseTimeTodoAuthorization` → `UseYourAppAuthorization`,
  `AddTimeTodoAuthorizationPolymorphism` → `AddYourAppAuthorizationPolymorphism`

### Step 2 — Adjust `Person` to your app's identity shape

Open `Principals/Person.cs`. Add/remove fields:
- TimeToDo has Firstname/Lastname/Acronym + AccountName + Email + ExternalIdentities
- Your app might want JobTitle/Department/EmployeeNumber instead — change them
- Default values, `DisplayName` getter, `GetEmailsAsync` — adjust freely

If your app's Person doesn't need `ExternalIdentities` (no IdP-integration),
delete that property and the `ExternalIdentityRef` record from the bottom of
the file.

### Step 3 — Adjust resources

In your app's DI setup:
```csharp
services.AddYourAppAuthorization(opt =>
{
    opt.RegisterResource("invoice", "read", "create", "approve");
    opt.RegisterResource("project", "read", "manage");
    opt.RegisterResource("app", "admin");
});
```

Resources are pure naming convention — pick what makes sense.

### Step 4 — Wire your Person events to the projection

The slice handles all group events and the polymorphic Principal table out of
the box. Person events are app-specific:

1. Define your events: `PersonCreatedEvent`, `PersonUpdatedEvent`,
   `PersonActivatedEvent`, etc. (Or copy TimeToDo's `UserCreatedEvent` etc.
   from `TimeToDo.Domain.Users.Events` and adjust.)
2. Subclass `PrincipalProjectionBase` and add `Create`/`Apply` for those events:
   ```csharp
   public class PrincipalProjection : PrincipalProjectionBase
   {
       public Principal Create(PersonCreatedEvent e) => new Person {
           Id = e.Id,
           AccountName = e.AccountName,
           // ... your fields ...
       };

       public Principal Apply(PersonUpdatedEvent e, Principal current) {
           if (current is not Person p) return current;
           // ... mutate p ...
           return p;
       }
   }
   ```
3. Register inline:
   ```csharp
   options.Projections.Add<PrincipalProjection>(ProjectionLifecycle.Inline);
   ```
4. Set stable `MapEventType` aliases for your Person events so future renames
   don't break the event store.

### Step 5 — Identity adapter

The Person events come from somewhere. TimeToDo emits them from an
ASP.NET-Identity adapter (`EventSourcedUserStore` in `TimeToDo.Infrastructure`).
If your app uses Identity too, copy that pattern. If you use Identity-Server,
Auth0, or your own login flow, write the equivalent.

### Step 6 — Frontend

The slice is backend-only. Frontend code (Vue / React / whatever) is not
included — TimeToDo's frontend is in `src/frontend-vue/`, with its own
auth-related views in `src/frontend-vue/src/views/admin/{user,role,group}`.
Treat that as inspiration, not a drop-in.

---

## Migration guide

When upgrading TimeToDo itself from a pre-extract version, see Section 11 of
`.local/prod-migration-guide.md` — covers schema changes, the bootstrap
projection rebuild via `dotnet TimeToDo.Api.dll recover rebuild-projections`,
and legacy table cleanup.

---

## What's NOT in this slice

- **Identity / Authentication** — login, password, 2FA, passkeys, magic
  links, OIDC. Lives in `TimeToDo.Domain/Identity` + `TimeToDo.Infrastructure/Identity`
  + `TimetoDo.Api/Features/Account`. Will become its own vertical slice
  (`TimeToDo.Authentication`) at some point.
- **User profile management** — display fields, change-requests, profile
  self-service. Lives in `TimetoDo.Api/Features/Account` + `Admin`.
- **Auth log / audit** — `AuthLogDocument` + `AuthLogSink` are TimeToDo-internal,
  not part of the slice.
- **Frontend** — see Step 6 above.

The split is intentional: this slice owns "**who has what permission, who's in
what group, who can see what data**." Authentication ("who is this person
making the request") is its own concern.
