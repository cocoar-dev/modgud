# Modgud.Authorization

Vertical slice for the authorization core of modgud. Standalone
C# project (`src/dotnet/Modgud.Authorization/`), copied from
TimeToDo's slice of the same name into modgud and extended with
IdP-specific resources.

## What is in the slice

- **Principals with capability interfaces** — `Person`, `Group`,
  `ServiceAccount`; everything addressable by id that can carry
  permissions
- **Roles + permissions** — RBAC with free resource/action
  registration via `IResourceRegistry`. modgud registers:
  `user`, `permission-role`, `authorization-group`, `oauth-client`,
  `oauth-scope`, `oauth-api`, `login-provider`, `idp-config`,
  `realm`, `auth-log`, `app` — all keyed under the `modgud` app
  slug
- **Granular per-resource gating** — `<resource>:<action>` strings
  inside an App's catalog (`user:read`, `oauth-client:write`); the App
  context is implicit from the caller / token audience. Exactly two
  bypass tiers: `<resource>:admin` (resource-wide within the calling
  app) and `realm:admin` (realm-wide emergency exit). For the canonical
  story see [Permissions & gating](/concepts/permissions).
- **Auto-Membership** — groups whose members are determined by a
  predicate script, including dependency tracking for selective
  recalculation. The script only sees IAM-owned fields (DisplayName,
  Email, IsActive, ExternalIdentities) — deliberately no app-schema
  fields, see [Concepts → ABAC](/concepts/abac)
- **ASP.NET Core extension** —
  `.RequiresPermission("oauth-client:write")` as an endpoint filter
  (the app catalog the gate runs against is implicit from the caller)
- **Marten integration** — polymorphic storage via sub-class mapping
  (Person + Group + ServiceAccount in the same `mt_doc_principal`
  table), inline projection for synchronous consistency, Wolverine
  commands for CRUD

## What the slice deliberately does not do

- **No authentication** — login, 2FA, passkey, OIDC live in the
  Authentication slice
- **No HTTP endpoints for CRUD** — the app defines its endpoints and
  dispatches via `IMessageBus.InvokeAsync<ErrorOr<Group>>(command)`
- **No storage abstraction** — Marten + Wolverine + event sourcing
  are fixed
- **No tenant routing** — the slice always works against the current
  Marten tenant session. Realm routing is done by `RealmMiddleware`
  in the Api layer

## Boundary against the Authentication slice

| Responsibility | Authorization slice | Authentication slice |
|---|---|---|
| Who is this user? | — | ✅ |
| What is the user's name? | Read model: `Person` (Firstname etc.) | ✅ Identity adapter populates it |
| Which groups are they in? | ✅ | — |
| Which roles do they have? | ✅ | — |
| May they do this? | ✅ | — |
| May they see this row? | — *(the app's job, see [ABAC](/concepts/abac))* | — |

`Person` is the bridge: identity-shaped fields are populated by the
Authentication stack (via the app-specific `PrincipalProjection` that
inherits `PrincipalProjectionBase`), and the Authorization slice uses
them as the read model for email routing and membership predicates.

## ResourceRegistry

The central hub for all permission strings. The Authorization slice
provides the interface; each app registers its resources at boot:

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
});
```

Per resource the standard actions `read`, `write`, `delete`, `admin`
are available. The admin UI shows this list in the role editor; the
backend checks the strings on `RequiresPermission`.

## Default roles on first bootstrap

When the first admin in a realm is created (recovery CLI or HTTP
bootstrap-invite — see [First-time setup](../../getting-started/first-time-setup)),
`RealmAdminBootstrapper` atomically seeds three default `PermissionRole`s
and places the new admin into the **Administratoren** group:

| Role | Permissions (within the `modgud` app's catalog unless noted) |
|---|---|
| **System Admin** | `IsRealmAdmin = true` → realm-wide bypass; group is `BoundTo: ["*"]` |
| **User Manager** | `user:read`, `user:write`, `permission-role:read`, `authorization-group:read`, `authorization-group:write` |
| **Viewer** | `user:read`, `permission-role:read`, `authorization-group:read`, `oauth-client:read`, `oauth-scope:read` |

## Dependencies

| Hard | Reason |
|---|---|
| Marten 9+ | Event store + polymorphic document storage (sub-class mapping) |
| WolverineFx.Marten | Commands + handler discovery + outbox |
| Cocoar.JsEval + .Linq + .TypeScript | TS → JS transpile + JS → expression-tree translation for membership scripts |
| ErrorOr | Command return types |
| Microsoft.AspNetCore.App | `IEndpointFilter` for `RequiresPermission` |

## Status

Modgud uses this slice in production. Wired through
`UseModgudAuthorization()` in the Marten configuration and
`services.AddModgudAuthorization()` in DI.

## Table of contents

- [Concepts](./konzepte) — mental model, polymorphism, events, projections
- [Permissions & gating](/concepts/permissions) — per-resource gating, sidebar, endpoint filter (public)
- [Auto-Membership](/concepts/auto-membership) — groups with predicate membership (public)
- [ABAC and the IAM boundary](/concepts/abac) — why row-level access stays in the app
