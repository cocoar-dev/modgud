# Applications

An **Application** in Cocoar.Auth is the organisational clamp around a SaaS app — it owns its own resources, its own roles, and its own OAuth bindings. When a realm is created the system app `cocoar-auth` (= Cocoar.Auth itself) is provisioned automatically; every other app you register here.

::: tip First time?
If this is your first SaaS-app integration, the [SaaS App Integration Walkthrough](./saas-integration-walkthrough) is the better entry point — it walks through all five stations (App, Client, Resource Server, Roles, backend code).
:::

## What is an Application for?

Cocoar.Auth manages permissions in the form `app:resource:action` — for instance `timetodo:todo:read` or `knowledge:article:write`. Every permission belongs to exactly one app.

An app therefore bundles:

- **Resources** — the business objects (`todo`, `project`, …)
- **Roles** with `AppSlug` — permission bundles for that app
- **Groups** via `BoundTo` — which organisational unit is active in which app
- **OAuth Clients** via their `AppIds` list — which token requesters serve the app
- **OAuth APIs (Resource Servers)** via their `AppId` — which backend identities belong to it
- **OAuth Scopes** via their `AppId` — which scopes a client of the app may request

## Application fields

| Field | Meaning |
| --- | --- |
| Slug | URL- and permission-safe identifier. Lowercase, 3-63 characters, letters/digits/hyphens. **Immutable after creation.** |
| Display Name | What appears in lists and consent screens |
| Description | Optional, one-liner |
| Resources | One per line. Together with the slug they form the permission vocabulary (`<slug>:<resource>:<action>`) |
| IsSystem | True only for `cocoar-auth`, cannot be deleted |

## Reserved slugs

These slugs are forbidden — they collide with the permission grammar:

- `realm` — would clash with `realm:admin` (realm-wide bypass)
- `*` — wildcard in `Group.BoundTo`
- `cocoar-auth` — system app, seeded automatically

## Creating an app

Click **Create** in the list view.

1. Pick a slug — kebab-case, memorable: `timetodo`, `knowledge`, `alert-hub`. Not changeable.
2. Fill in display name and description.
3. Resources: one per line, also kebab-case: `todo`, `project`, `tag`. Can be extended later.
4. **Create**.

The app appears in the list. **It still has no effect** on its own — you also need to:

- link at least one OAuth client to it ([OAuth Clients](./oauth-clients))
- (for the distribution API) provision a resource server — see the click-action below
- create at least one role + group that connects users to the app

## Click-action: provision the default resource server

In an app's detail modal (except `cocoar-auth`) you'll find a **Resource Server** section at the bottom with a **Create default resource server** button.

What happens on click:

1. A new OAuth API named after the app slug is created
2. It is linked to the app (`AppId`)
3. An initial **API secret is returned exactly once**

**Copy that secret immediately** — Cocoar.Auth only stores its hash, you'll never see the cleartext again.

What is it for? When your app's backend calls the distribution API (`/api/v1/distribution/me-permissions`), it identifies itself to Cocoar.Auth via:

```
X-Resource-Server-Id: <app-slug>
X-Resource-Server-Secret: <secret-from-the-click>
```

Pressing the button again on an app that already has a default resource server: Cocoar.Auth says "Already exists" — no new secret, no second RS.

::: tip When do I not need a default RS?
If your app only checks coarse roles (`[Authorize(Roles = "Admin")]`) and never makes live permission lookups, the OAuth client + UserInfo claims are enough; you can skip the default RS.
:::

## Extending or changing resources

Resources can be edited any time, but:

- **Adding** is harmless. Existing permissions remain valid; new ones become usable.
- **Removing** is dangerous. Roles that reference the removed resource still expand to a permission tuple — but no consumer recognises it any more. Audit roles before dropping a resource.

## Relationships to other areas

| Linked with | Where | How |
| --- | --- | --- |
| OAuth Clients | [OAuth Clients](./oauth-clients) | n:m via the client's `AppIds` list |
| OAuth Scopes | [OAuth Scopes](./oauth-scopes) | 1:n via the scope's `AppId` (or null = global) |
| OAuth APIs (Resource Servers) | [OAuth APIs](./oauth-apis) | 1:n via the API's `AppId` |
| Roles | [Roles](./roles) | n:1 via the role's `AppSlug` |
| Groups | [Groups](./groups) | n:m via the group's `BoundTo` list |

## The system app cocoar-auth

The app `cocoar-auth` represents Cocoar.Auth itself. Permissions like `cocoar-auth:user:read` or `cocoar-auth:oauth-client:write` are what gate the admin UI's sidebar.

It is:

- **Auto-seeded** on first realm setup
- **Not deletable** (IsSystem = true)
- **Slug not renameable** (always `cocoar-auth`)
- Resources match the built-in admin surface — edit cautiously

If you change `cocoar-auth` resources, the admin sidebar may hide items because the corresponding permissions no longer exist. When in doubt, restore the default resource list (see `AppRealmSeeder` in source).

## Deleting an app

System apps cannot be deleted. Regular apps can — but:

- OAuth clients with the app in their `AppIds` list keep the entry (UI shows it as "unknown app")
- OAuth scopes with this AppId become orphaned
- Roles with this AppSlug stay — but their permissions are dead
- Groups with the app in BoundTo keep the entry, but it no longer has effect

So before deleting: re-link or delete the dependent clients, scopes, and roles first.
