# Apps and resource_access

This page explains the mental model behind Cocoar.Auth's permission system: what an "App" is, how it relates to OAuth concepts, how the Keycloak-style `resource_access` claim is shaped, and how the permission resolver gets from a logged-in user to a concrete answer.

## The four-axis model

OAuth/OIDC officially knows four roles: Resource Owner (the user), Client, Authorization Server, Resource Server. Cocoar.Auth adds a fifth concept that the OAuth spec doesn't model — the **App**.

```
                          Realm
                            │
                ┌───────────┴───────────────┐
                │                            │
            Identity                       Apps
                │                       (the IAM axis)
   ┌────────────┼─────────────┐          │
   │            │             │     ┌────┼─────────────┐
 Users       Groups      PermRoles  │    │             │
                                   App  Resources    Roles
                                                 (per app)
                                    │
                            ┌───────┼───────┐
                            │       │       │
                       OAuth      OAuth   Scopes
                       Clients    APIs    (per app)
                       (n:m)      (1:n)
```

Why an App layer? Because in OAuth a **Resource Server** is just a Resource Server — TimeToDo-API is one thing. But organisationally, "TimeToDo as a product" might be many resource servers (api, search, files), share resources/roles across them, and need a coherent permission story regardless of which microservice the user is hitting. **The App is the organisational clamp.**

| Concept | Purpose | OAuth analog |
| --- | --- | --- |
| **App** | Organisational identity for a SaaS product, owns Resources + Roles | none (Cocoar-specific) |
| **OAuth Client** (`OAuthApplication`) | Identity that requests tokens (frontend, CLI, mobile) | OAuth Client |
| **OAuth API** (`OAuthApi`) | Identity that authenticates as a resource server | OAuth Resource Server |
| **OAuth Scope** | What a token may do (gross-grained) | OAuth Scope |
| **Group** | Org-level user collection (mailing-list semantics) | none |
| **PermissionRole** | Bundle of permissions for one app | Role |
| **Permission** | Smallest unit, shape `app:resource:action` | Permission |

Every artefact below the realm sits on one of these axes. App-scoped artefacts (PermissionRole.AppSlug, OAuthScope.AppId, OAuthApi.AppId) reach back up to the App; Group.BoundTo is the activation switch ("is this group active in app X?").

## Why apps and resource servers aren't 1:1

Two real-world deviations from "one App = one Resource Server":

**Microservice apps.** TimeToDo's backend might be split into `timetodo-api`, `timetodo-search`, `timetodo-files`. All three are different OAuth API identities (each with its own secret, its own audit identity), but they share the same App `timetodo` — so a user is `Editor in TimeToDo` and that role works regardless of which microservice handles a given HTTP request.

**Multi-app frontends.** A unified webshop frontend might call into a `shop` app, a `payments` app, and an `inventory` app. The frontend has *one* OAuth Client (one user-facing identity), but the client is linked to all three Apps via its `AppIds` list. The issued token then carries `resource_access` blocks for all three; each backend reads its own block.

The two flexibilities together let Cocoar.Auth represent any reasonable architecture without forcing you into "everything is one app" or "split everything into separate clients".

## Permission resolution: step by step

Given a `(userId, appSlug)` pair (e.g. `(bernhard, "timetodo")`), what permissions does the user effectively hold?

```
1. BFS user → groups        (transitive: User in A; A in B; A and B both count)
2. Filter groups            (g.BoundTo contains "*" OR appSlug)
3. Collect role IDs         (g.RoleIds for each surviving group)
4. Load roles               (drop deleted)
5. Expand each role's permissions:
     - bare "read"          → {role.AppSlug}:{role.ResourceType}:read
                              (only contributes when role.AppSlug == appSlug)
     - "realm:admin"        → passes through (cross-app bypass)
     - "x:y:z"              → passes through (fully-qualified explicit)
6. Distinct → result
```

Two filters, not one: BoundTo on the group, AppSlug on the role. They serve different purposes — BoundTo is "is this group active here?", AppSlug is "is this role about this app?".

The resolver lives in `Cocoar.Auth.Authorization.Services.PermissionService`. It runs on the IDP (not at token-issue time, but at /me-permissions and any future server-side query).

## The token shape

When a user logs in via an OAuth Client linked to apps `[timetodo, knowledge]`, the access token's UserInfo response (when the `roles` scope was granted) contains a Keycloak-style nested claim:

```json
{
  "sub":   "abc123…",
  "email": "bernhard@cocoar.dev",
  "name":  "Bernhard",

  "resource_access": {
    "timetodo": {
      "roles": ["Editor"]
    },
    "knowledge": {
      "roles": ["Viewer"]
    }
  }
}
```

Each resource server reads its own block. TimeToDo-API sees `resource_access["timetodo"].roles = ["Editor"]`; Knowledge-API sees `resource_access["knowledge"].roles = ["Viewer"]`. Neither sees the other's data magnified — they each have it side-by-side, but consume just their own.

Group memberships are **not** in the token. They live behind the [distribution API](../reference/distribution-api.md) — the IDP/IAM cut keeps the token focused on identity-shape data, while organisational structure stays IAM-side.

## What's *not* in the token

Three things are deliberately absent from UserInfo:

- **Granular permissions** (`timetodo:todo:write`). They change too quickly and there can be too many to make a JWT claim sensible. Live-resolved via the distribution API.
- **Group memberships**. Organisational signal, not authorisation. Also app-scoped via BoundTo, which UserInfo's flat shape can't express cleanly. Distribution-API-side instead.
- **Cross-app roles for apps the calling client isn't linked to**. The token only carries `resource_access` blocks for the apps the issuing client knows about.

Anything that's both grouped under "what may this user do" and stable enough to ride along with the identity → goes in the token. Everything else → distribution API.

## Design decisions worth knowing

These are non-obvious choices the resolver makes. Knowing them avoids "why doesn't this work" moments:

**`Group.BoundTo = []` ≠ `BoundTo = ["*"]`.** Empty means *dormant for permission purposes* — the group exists for org/mailing-list reasons but contributes zero to authorisation. Wildcard means *active in every app* (rare, mostly the realm-admin group).

**Permissions are not cascaded when BoundTo changes.** Removing an app from `BoundTo` *deactivates* the group in that app — it does NOT strip the group's roles. You can re-add the app and the group is immediately active again. Reduces accidental data loss in admin operations.

**`Role.AppSlug` is fixed.** Once a role is created, its app affiliation cannot change — moving permissions across apps means cloning the role under a new AppSlug. Rare operation, easy to spot in audit logs.

**Fully-qualified permissions are pass-through escape hatches.** A role normally expands `["read", "write"]` against its AppSlug + ResourceType. If you stick `realm:admin` directly into the permissions list, it passes through as-is — that's the mechanism `System Admin` uses to grant cross-app bypass.

**`OAuthApplication.AppIds` is `n:m` (a client can be linked to many apps).** **`OAuthApi.AppId` is `1:1` (a resource server belongs to one app).** Asymmetric on purpose: client-side aggregation (one frontend, many resource servers) is normal; server-side aggregation would muddle the audit trail.

## Glossary

- **Realm** — top-level mandant. Own database, own users, own apps.
- **App** — organisational identity for a SaaS product within a realm.
- **OAuth Client** — token requester. Has `AppIds: List<Guid>` (n:m).
- **OAuth API** — token-validating server identity. Has `AppId: Guid` (1:1).
- **OAuth Scope** — gross-grained capability claim. Has `AppId: Guid?` (null = global, e.g. `openid`).
- **Group** — user collection. Has `BoundTo: string[]` (which apps it's active in).
- **PermissionRole** — bundle of permissions. Has `AppSlug: string` (which app it belongs to).
- **Permission** — `app:resource:action` string.
- **`resource_access`** — Keycloak-style nested UserInfo claim, keyed by app slug.
- **Distribution API** — `/api/v1/distribution/*`, server-to-server endpoints requiring user-bearer + RS-Auth.
