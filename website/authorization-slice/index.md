# Cocoar.Auth.Authorization

Vertical slice for the authorization core of cocoar.auth. Standalone
C# project (`src/dotnet/Cocoar.Auth.Authorization/`), copied from
TimeToDo's slice of the same name into cocoar.auth and extended with
IdP-specific resources.

## What is in the slice

- **Principals with capability interfaces** — `Person`, `Group`,
  `ServiceAccount`; everything addressable by id that can carry
  permissions
- **Roles + permissions** — RBAC with free resource/action
  registration via `IResourceRegistry`. cocoar.auth registers:
  `user`, `permission-role`, `authorization-group`, `oauth-client`,
  `oauth-scope`, `oauth-api`, `login-provider`, `idp-config`,
  `realm`, `auth-log`, `app` — all keyed under the `cocoar-auth` app
  slug
- **Granular per-resource gating** — `<app>:<resource>:<action>`
  strings (`cocoar-auth:user:read`, `cocoar-auth:oauth-client:write`).
  Three bypass tiers: `<app>:<resource>:admin` (resource-wide within
  one app), `<app>:admin` (app-wide), `realm:admin` (realm-wide
  emergency exit)
- **Auto-Membership** — groups whose members are determined by a
  predicate script, including dependency tracking for selective
  recalculation. The script only sees IAM-owned fields (DisplayName,
  Email, IsActive, ExternalIdentities) — deliberately no app-schema
  fields, see [Concepts → ABAC](/concepts/abac)
- **ASP.NET Core extension** —
  `.RequiresPermission("cocoar-auth:oauth-client:write")` as an endpoint filter
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
services.AddCocoarAuthAuthorization(opts =>
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
    opts.RegisterResource("app");  // for app-wide bypass `cocoar-auth:admin`
});
```

Per resource the standard actions `read`, `write`, `delete`, `admin`
are available. The admin UI shows this list in the role editor; the
backend checks the strings on `RequiresPermission`.

## Default roles in setup

On first-time setup, cocoar.auth creates three default roles and
places the first admin in the "System Admin" group:

| Role | Permissions |
|---|---|
| **System Admin** | `realm:admin` (realm-wide bypass; group is `BoundTo: ["*"]`) |
| **User Manager** | `cocoar-auth:user:read`, `cocoar-auth:user:write`, `cocoar-auth:permission-role:read`, `cocoar-auth:authorization-group:read`, `cocoar-auth:authorization-group:write` |
| **Viewer** | `cocoar-auth:user:read`, `cocoar-auth:permission-role:read`, `cocoar-auth:authorization-group:read`, `cocoar-auth:oauth-client:read`, `cocoar-auth:oauth-scope:read` |

## Dependencies

| Hard | Reason |
|---|---|
| Marten 8+ | Event store + polymorphic document storage (sub-class mapping) |
| WolverineFx.Marten | Commands + handler discovery + outbox |
| Cocoar.JsEval + .Linq + .TypeScript | TS → JS transpile + JS → expression-tree translation for membership scripts |
| ErrorOr | Command return types |
| Microsoft.AspNetCore.App | `IEndpointFilter` for `RequiresPermission` |

## Status

Cocoar.Auth uses this slice in production. Wired through
`UseCocoarAuthAuthorization()` in the Marten configuration and
`services.AddCocoarAuthAuthorization()` in DI.

## Table of contents

- [Concepts](./konzepte) — mental model, polymorphism, events, projections
- [Permissions & gating](./permissions) — per-resource gating, sidebar, endpoint filter
- [Auto-Membership](./auto-membership) — groups with predicate membership
- [ABAC and the IAM boundary](/concepts/abac) — why row-level access stays in the app
