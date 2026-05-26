# Architecture — slice blueprints

Two vertical slices form the IAM backbone of Modgud. Both are
standalone C# projects under `src/dotnet/`, consumed by `Modgud.Api`
via `ProjectReference`. The split exists so the IAM core can move
independently of the IdP-specific layers (realms, OAuth aggregates,
OpenIddict stores) stacked on top.

| Slice | Project | Responsibility |
|---|---|---|
| [Authentication](./authentication) | `Modgud.Authentication` | *Who is this user?* — login methods, 2FA, OIDC federation, sessions, GDPR, AuthLog, recovery CLI |
| [Authorization](./authorization) | `Modgud.Authorization` | *May they do this?* — principals, roles, permissions, auto-membership, endpoint filter |

## Boundary

| Responsibility | Authentication | Authorization | Modgud.Api |
|---|---|---|---|
| Who is this user? | ✅ | — | — |
| What is the user's name / email? | ✅ — Identity adapter populates it | Read model: `Person.DisplayName/Email/...` | — |
| Which groups are they in? | — | ✅ | — |
| Which roles do they have? | — | ✅ | — |
| May they do this? | — | ✅ | — |
| May they see *this row*? | — | — *(the consuming app's job; see `/concepts/abac`)* | — |
| Which realm? | — | — | `RealmMiddleware` sets the Marten tenant |
| Realm CRUD, OAuth aggregates, OpenIddict stores | — | — | ✅ |

## The PrincipalProjectionBase bridge

The two slices are connected through a single seam:
`PrincipalProjectionBase` — an abstract Marten projection in
`Modgud.Authorization`. The authentication slice inherits it
(`AuthPrincipalProjection`) and adds Apply methods for user events
(`UserCreatedEvent`, `UserUpdatedEvent`, …). The combined projection
materialises `Person` documents into the authorization slice's
`mt_doc_principal` table via sub-class mapping.

```
┌─────────────────────────────┐         ┌──────────────────────────────┐
│ Modgud.Authentication       │         │ Modgud.Authorization         │
│   UserCreatedEvent          │         │   Principal (abstract)       │
│   UserUpdatedEvent      ────┼──────► │   ├─ Person (subclass)       │
│   UserGdprErasedEvent       │         │   ├─ Group  (subclass)       │
│                             │         │   └─ ServiceAccount …        │
│   GroupCreatedEvent     ────┼──────► │   PrincipalProjectionBase    │
│   GroupUpdatedEvent         │         │     (inline projection)      │
└─────────────────────────────┘         └──────────────────────────────┘
                          │                          │
                          ▼                          ▼
                  ┌──────────────────────────────────────┐
                  │ mt_doc_principal (Marten — per realm)│
                  └──────────────────────────────────────┘
```

This is the only place where the slices touch. Everything else flows
through interfaces (`IAuthSettings`, `IServerConfiguration`,
`IPermissionResolver`, …) registered by the Api layer.

## Reading order

If you are reading these blueprints to extend the IAM core:

1. **`authorization.md`** first — it defines the `Principal` polymorphic
   model and the projection seam.
2. **`authentication.md`** next — it explains how identity events flow
   into the polymorphic table on the other side and how all of the
   user-facing surface area is built.

If you are debugging a runtime issue, jump directly to the slice that
owns the relevant entry point (see the table at the top).
