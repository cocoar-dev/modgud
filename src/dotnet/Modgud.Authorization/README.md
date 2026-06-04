# Modgud.Authorization

A **vertical slice** owning everything authorization-related in Modgud:
principals (Person + Group + ServiceAccount), permissions, role-based grants,
group-membership resolution (incl. auto-membership via JsEval-scripted
predicates), and resource-specific access policies.

> **This is not a reusable library.** It is a self-contained reference
> implementation — designed for **copy + adapt** when the same authorization
> shape is needed in another app. No abstract extension points, no
> generic hooks, no API stability promises across apps. Adopting apps own
> their copy and modify it freely.

---

## Why a separate `csproj` if it's not a library?

Three reasons that have nothing to do with reusability:

1. **Boundary enforcement.** The compiler stops anyone in `Modgud.Api` from
   reaching into authorization internals — only what's `public` is visible.
2. **Explicit dependency direction.** `Modgud.Domain → Modgud.Authorization`
   is recorded in csproj references. Reversed accidents are caught at build time.
3. **Adoption clarity.** When pointing at "the authorization slice from
   Modgud," the answer is one folder + one csproj. No file-hunting across
   `Domain/Identity`, `Infrastructure/Identity`, `Api/Features/Groups`, etc.

---

## Public surface (what consumers call)

```
Modgud.Authorization.Setup.ServiceCollectionExtensions
   .AddModgudAuthorization(opts => opts.RegisterResource(...))

Modgud.Authorization.Setup.MartenStoreOptionsExtensions
   .UseModgudAuthorization()                        // on StoreOptions
   .AddModgudAuthorizationPolymorphism()            // on JsonSerializerOptions

Modgud.Authorization.AspNetCore.PermissionEndpointFilter
   .RequiresPermission("resource:action")           // RouteHandlerBuilder ext

Modgud.Authorization.Services
   IPermissionService                                // BFS Principal → Group → Role
   IPrincipalLookupService                           // by-id + lightweight lookup
   IPrincipalEmailResolver                           // shared / expand-to-members
   IMembershipEvaluator                              // auto-membership scripts
   IAutoMembershipRecalculator                       // recompute on principal change

Modgud.Authorization.Commands
   CreateGroupCommand / CreateGroupHandler           // Wolverine
   UpdateGroupCommand / UpdateGroupHandler

Modgud.Authorization.Events
   GroupCreatedEvent / GroupUpdatedEvent / GroupDeletedEvent
   GroupMembershipRecomputedEvent / GroupMembershipRecomputeFailedEvent
   PermissionRoleCreatedEvent / PermissionRoleUpdatedEvent / PermissionRoleDeletedEvent

Modgud.Authorization.Principals
   Principal (abstract) → Person, Group, ServiceAccount (all concrete)
   IPrincipal, IPrincipalWithAccount, IPrincipalWithMembers, IPrincipalEmailAddressable
   ExternalIdentityRef
   MembershipMode (Manual/Auto), EmailMode (Shared/ExpandToMembers)

Modgud.Authorization.Roles
   PermissionRole                                    // doc

Modgud.Authorization.Resources
   IResourceRegistry, ResourceRegistry               // declares "todo: read,create,..."

Modgud.Authorization.Projections
   PrincipalProjectionBase                           // group events → mt_doc_principal
   PermissionRoleProjection                          // role events → mt_doc_permissionrole
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
                              Membership (Manual/Auto + script),
                              Email (Shared/ExpandToMembers)

  Principal → Group → PermissionRole → Permissions ("resource:action")
                  ▲ BFS over MemberIds (transitive groups)

  Auto-membership: Group's TypeScript predicate
    (p) => Type.Is(p, 'person') && p.Firstname.startsWith('A')
  → JsEval transpiles to LINQ → Marten emits SQL filter on mt_doc_principal
```

**Storage shape:**
- `mt_doc_principal` — polymorphic table, holds Person + Group + ServiceAccount
  docs, distinguished by Marten's `mt_doc_type` discriminator (`"person"` /
  `"group"` / `"service_account"`)
- `mt_doc_permissionrole` — flat doc table for roles
- `mt_events` — event store, all auth events use stable snake_case aliases:
  `authorization_group_*`, `permission_role_*`

---

## How Modgud wires it (look here when copying)

`src/dotnet/Modgud.Infrastructure/DependencyInjection.cs` :: `AddInfrastructure`

```csharp
services.AddModgudAuthorization(opt =>
{
    opt.RegisterResource("user", "read", "write");
    opt.RegisterResource("session", "read", "write");
    opt.RegisterResource("oauth-client", "read", "write");
    opt.RegisterResource("app", "admin");
    // ... etc — see AppRealmSeeder for the full Modgud catalogue
});
```

`src/dotnet/Modgud.Infrastructure/Persistence/Marten/Configuration/MartenConfiguration.cs`

```csharp
options.UseSystemTextJsonForSerialization(EnumStorage.AsString, configure: o => {
    o.AddOptionalAware();
    o.AddModgudAuthorizationPolymorphism();   // ← STJ $type for Principal
});
options.UseModgudAuthorization();             // ← sub-class mapping + event aliases
```

That's the entire wiring footprint. Person events (`UserCreatedEvent` etc.)
are emitted by the `Modgud.Authentication` slice (the ASP.NET-Identity
adapter `EventSourcedUserStore`) and bridged into the polymorphic
`Principal` table via `AuthPrincipalProjection` (which extends
`PrincipalProjectionBase`).

---

## Adoption guide — copying this into another app

Goal: stand up the same authorization model in a new app, adapted to its
needs.

### Step 1 — Copy the project

```bash
cp -r src/dotnet/Modgud.Authorization YourApp/YourApp.Authorization
```

Rename:
- Folder name → `YourApp.Authorization`
- `Modgud.Authorization.csproj` → `YourApp.Authorization.csproj`
- Inside csproj: `<RootNamespace>` and `<AssemblyName>` to `YourApp.Authorization`
- All `namespace Modgud.Authorization.*` → `namespace YourApp.Authorization.*`
- All `using Modgud.Authorization.*` in your app code → `using YourApp.Authorization.*`
- Extension method names if you want: `AddModgudAuthorization` → `AddYourAppAuthorization`,
  `UseModgudAuthorization` → `UseYourAppAuthorization`,
  `AddModgudAuthorizationPolymorphism` → `AddYourAppAuthorizationPolymorphism`

### Step 2 — Adjust `Person` to your app's identity shape

Open `Principals/Person.cs`. Add/remove fields:
- Modgud has Firstname/Lastname/Acronym + AccountName + Email + ExternalIdentities
- Your app might want JobTitle/Department/EmployeeNumber instead — change them
- Default values, `DisplayName` getter, `GetEmailsAsync` — adjust freely

If your app's Person doesn't need `ExternalIdentities` (no IdP integration),
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

The slice handles all group events and the polymorphic Principal table out
of the box. Person events are app-specific:

1. Define your events: `PersonCreatedEvent`, `PersonUpdatedEvent`,
   `PersonActivatedEvent`, etc. (Or copy Modgud's `UserCreatedEvent` etc.
   from `Modgud.Authentication` and adjust.)
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

The Person events come from somewhere. Modgud emits them from an
ASP.NET-Identity adapter (`EventSourcedUserStore` in
`Modgud.Authentication`). If your app uses Identity too, copy that pattern.
If you use Identity-Server, Auth0, or your own login flow, write the
equivalent.

### Step 6 — Frontend

The slice is backend-only. Frontend code (Vue / React / whatever) is not
included — Modgud's frontend is in `src/frontend-vue/`, with its own
auth-related views in `src/frontend-vue/src/views/admin/{user,role,group}`.
Treat that as inspiration, not a drop-in.

---

## What's NOT in this slice

- **Identity / Authentication** — login, password, 2FA, passkeys, magic
  links, OIDC. Lives in the `Modgud.Authentication` slice.
- **User profile management** — display fields, change-requests, profile
  self-service. Lives in `Modgud.Api/Features/Account` + `Admin`.
- **Auth log / audit** — the `AuthAuditView` projection (GDPR-audit) and the
  `SecurityAuditEntry` streamless security store are Modgud-internal, not part
  of the slice.
- **Frontend** — see Step 6 above.

The split is intentional: this slice owns "**who has what permission, who's
in what group, who can see what data**." Authentication ("who is this
person making the request") is its own concern.
