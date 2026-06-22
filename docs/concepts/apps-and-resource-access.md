# Apps and resource_access

This page explains the mental model behind Modgud's permission system:
what an "App" is, how it relates to OAuth concepts, how the
Keycloak-style `resource_access` claim is shaped, and how the
permission resolver gets from a logged-in user to a concrete answer.

## The four-axis model

OAuth/OIDC officially knows four roles: Resource Owner (the user),
Client, Authorization Server, Resource Server. Modgud adds a fifth
concept that the OAuth spec doesn't model — the **App**.

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

Why an App layer? Because in OAuth a **Resource Server** is just a
Resource Server — `acme-api` is one thing. But organisationally, "Acme
as a product" might be many resource servers (api, search, files),
share resources/roles across them, and need a coherent permission
story regardless of which microservice the user is hitting. **The App
is the organisational clamp.**

| Concept | Purpose | OAuth analog |
| --- | --- | --- |
| **App** | Organisational identity for a SaaS product, owns Resources + Roles | none (IAM-specific) |
| **OAuth Client** (`OAuthApplication`) | Identity that requests tokens (frontend, CLI, mobile) | OAuth Client |
| **OAuth API** (`OAuthApi`) | Identity that authenticates as a resource server | OAuth Resource Server |
| **OAuth Scope** | What a token may do (gross-grained) | OAuth Scope |
| **Group** | Org-level user collection (mailing-list semantics) | none |
| **PermissionRole** | Bundle of permissions for one app | Role |
| **Permission** | Smallest unit, shape `<resource>:<action>` within an app catalog | Permission |

Every artefact below the realm sits on one of these axes. App-scoped
artefacts (`PermissionRole.AppId`, `OAuthScope.AppId`,
`OAuthApi.AppId`) reach back up to the App; `Group.BoundTo` is the
activation switch ("is this group active in app X?").

## The App's second facet: a login experience (ADR-0011)

The permission clamp above is the App's *first* facet. Since ADR-0011 an
App is also a **soft origin/login facet** within the realm: an optional
own subdomain, branding, email branding, and sparse per-app overrides of
self-registration / native-grant / DCR / CIMD policy.

The key word is **soft**. The realm (tenant) stays the **hard** boundary —
own database, signing keys, OIDC issuer, user pool, apex. An App does not
get its own user pool or its own issuer: all apps in a realm share one
account namespace (one `sub` everywhere, no shadow users), and a request
on an App subdomain still mints tokens under the **tenant's** canonical
issuer (the realm primary domain). The App only changes the *experience*
(which host serves login, what it's branded as, whether unknown emails can
self-register) — never the identity of the user behind it.

Resolution is by signal, ordered in time: **Host** (an App subdomain pins
the App) → **`client_id`** (a client's `AppIds`) → **scope/audience**. The
first signal wins and later signals must be consistent with it (Host pins
App X + a `client_id` for App Y → rejected). The settings themselves live
in a tenant-scoped `ApplicationSettings` document and merge field-by-field
over the realm defaults. See [Admin → Applications](../admin/applications#application-settings).

## Why apps and resource servers aren't 1:1

Two real-world deviations from "one App = one Resource Server":

**Microservice apps.** Acme's backend might be split into `acme-api`,
`acme-search`, `acme-files`. All three are different OAuth API
identities (each with its own secret, its own audit identity), but
they share the same App `acme` — so a user is `Editor in Acme` and
that role works regardless of which microservice handles a given HTTP
request.

**Multi-app frontends.** A unified webshop frontend might call into a
`shop` app, a `payments` app, and an `inventory` app. The frontend has
*one* OAuth Client (one user-facing identity), but the client is
linked to all three Apps via its `AppIds` list. The issued token then
carries `resource_access` blocks for all three; each backend reads its
own block.

The two flexibilities together let Modgud represent any reasonable
architecture without forcing you into "everything is one app" or
"split everything into separate clients".

## Permission resolution: step by step

Given a `(userId, appSlug)` pair (e.g. `(alice, "acme")`), what
permissions does the user effectively hold?

```
1. BFS user → groups        (transitive: User in A; A in B; A and B both count)
2. Filter groups            (g.BoundTo contains "*" OR appSlug)
3. Collect role IDs         (g.RoleIds for each surviving group)
4. Load roles               (drop deleted)
5. Filter to this app       (r.AppId == app.Id  OR  r.IsRealmAdmin)
6. For each role: resolve PermissionIds → catalog strings
                              ("invoice:read", "invoice:write", …)
7. Bypass-pre-expand:
     - realm:admin          → all catalog strings of every reachable App
     - <r>:admin            → all <r>:<a> strings in this app's catalog
8. Distinct → result
```

Two filters, not one: BoundTo on the group, AppId on the role. They
serve different purposes — BoundTo is "is this group active here?",
AppId is "is this role about this app?".

The resolver lives in
`Modgud.Authorization.Services.PermissionService`. It runs IdP-side
both for in-process gates and for the per-Audience `resource_access`
block on `/connect/userinfo`.

## The token shape

When a user logs in via an OAuth Client linked to apps `[billing,
shipping]`, the access token's `/connect/userinfo` response (with
appropriate scopes granted) contains a Keycloak-style nested claim:

```json
{
  "sub":   "abc123…",
  "email": "alice@example.com",
  "name":  "Alice",

  "resource_access": {
    "billing": {
      "roles": ["Editor"],
      "permissions": ["invoice:read", "invoice:write"]
    },
    "shipping": {
      "roles": ["Viewer"],
      "permissions": ["shipment:read"]
    }
  }
}
```

Each resource server reads its own block. The Billing-API sees
`resource_access["billing"]`; the Shipping-API sees
`resource_access["shipping"]`. Neither sees the other's data
magnified — they each have it side-by-side, but consume just their
own.

The `Modgud.Client.AspNetCore` helper lib's `IClaimsTransformation`
takes the matching audience block and flattens its roles onto
`ClaimTypes.Role`, so `[Authorize(Roles="Editor")]` works out of the
box without per-endpoint plumbing.

### What gets emitted is opt-in by scope

- `scope=roles` → emit the `roles` array per Audience block.
- `scope=permissions` → emit the `permissions` array per Audience
  block (bypass-pre-expanded and narrowed to that RS's
  `OAuthApi.PermissionIds` subset).

Without those scopes, the block is omitted (or empty). Clients ask
for exactly what they need; tokens stay lean.

### Per-RS subset narrowing

Each `resource_access` block is narrowed to the OAuthApi's declared
`PermissionIds` subset of the App's catalog. A microservice within a
multi-resource-server App only sees the permissions it declared as
its gating surface — strings from a sibling microservice within the
same App are excluded. No cross-RS leaks.

## What's *not* in the token

A few things are deliberately absent from UserInfo:

- **Group memberships.** Organisational signal, not authorisation.
  Also app-scoped via BoundTo, which UserInfo's flat shape can't
  express cleanly. Groups stay IAM-side.
- **Cross-app roles for apps the calling client isn't linked to.**
  The token only carries `resource_access` blocks for the apps the
  issuing client knows about.
- **`realm:admin` as a literal string.** It's bypass-pre-expanded
  into concrete catalog strings before emission, so consumers do
  straight exact-match without needing to mirror the evaluator's
  bypass logic.

Anything that's "what may this user do" and stable enough to ride
along with the identity → goes in the token. Group memberships and
other organisational signal → IAM admin endpoints.

## Design decisions worth knowing

These are non-obvious choices the resolver makes. Knowing them avoids
"why doesn't this work" moments:

**`Group.BoundTo = []` ≠ `BoundTo = ["*"]`.** Empty means *dormant for
permission purposes* — the group exists for org/mailing-list reasons
but contributes zero to authorisation. Wildcard means *active in every
app* (rare, mostly the realm-admin group).

**Permissions are not cascaded when BoundTo changes.** Removing an app
from `BoundTo` *deactivates* the group in that app — it does NOT strip
the group's roles. You can re-add the app and the group is immediately
active again. Reduces accidental data loss in admin operations.

**`Role.AppId` is fixed.** Once a role is created, its app affiliation
cannot change — moving permissions across apps means cloning the role
under a new AppId. Rare operation, easy to spot in audit logs.

**Bypass tiers are pre-expanded server-side.** Token consumers never
see `realm:admin` or `<r>:admin` as literal strings — Modgud expands
them into the concrete catalog entries before emission. The client
just checks `permissions.includes("invoice:write")` and is done.

**`OAuthApplication.AppIds` is `n:m` (a client can be linked to many
apps).** **`OAuthApi.AppId` is `1:1` (a resource server belongs to
one app).** Asymmetric on purpose: client-side aggregation (one
frontend, many resource servers) is normal; server-side aggregation
would muddle the audit trail.

## Glossary

- **Realm** — top-level tenant. Own database, own users, own apps.
- **App** — organisational identity for a SaaS product within a
  realm.
- **OAuth Client** — token requester. Has `AppIds: List<Guid>`
  (n:m).
- **OAuth API** — token-validating server identity. Has
  `AppId: Guid` (1:1).
- **OAuth Scope** — gross-grained capability claim. Has
  `AppId: Guid?` (null = global, e.g. `openid`).
- **Group** — user collection. Has `BoundTo: string[]` (which apps
  it's active in).
- **PermissionRole** — bundle of permissions. Has `AppId: Guid?`
  (null when `IsRealmAdmin = true`).
- **Permission** — `<resource>:<action>` string within one App's
  catalog. App context is implicit from the catalog container.
- **`resource_access`** — Keycloak-style nested UserInfo claim,
  keyed by app slug, with bypass-pre-expanded permissions narrowed
  per-RS.
