# SaaS App Integration Walkthrough

This page takes you from a freshly installed Modgud all the way to a
working external app doing single-sign-on against Modgud and reading
per-Audience permission claims out of `/connect/userinfo`.

> **Audience:** realm admins and developers integrating a SaaS app.
> Regular end-user onboarding is documented in
> [first steps](../end-user/first-steps).

## Conceptual overview

Modgud models the world in three layers:

- **Realm** — a tenant. Own database, own users, own apps. Setup
  automatically creates the `system` realm.
- **App** — a SaaS application within a realm (e.g. `modgud`, `acme`,
  `billing`). Each app owns its permission catalog and links to zero
  or more OAuth clients and resource servers.
- **Group / Role / Permission** — who may do what in which app.
  Groups bundle users and roles, roles bundle permissions,
  permissions are `<resource>:<action>` strings within an App's
  catalog (the App context is implicit).

When you bind a new SaaS app you traverse **five stations**:

1. Register the app
2. Create an OAuth client for the app's frontend
3. Create the resource server (OAuth API), link it to the app, and mint its resource-bearing scope
4. Optional: create roles + assign to a group
5. Configure the resource-server code in the SaaS app's backend

## Prerequisites

You need:

- A running Modgud instance (see
  [Getting Started](../getting-started/quickstart))
- An admin account (a member of the `Administrators` group, created
  via the [first-time bootstrap](../getting-started/first-time-setup))
- A URL for your target app (for redirect URIs), e.g.
  `https://acme.dev.local`

## Station 1: register the app

Navigate to **Administration → Applications**. You'll see at least the
system app `modgud`.

Click **Create**.

| Field | Example | Explanation |
| --- | --- | --- |
| Slug (immutable) | `acme` | Permission catalog container, kebab-case. **Cannot be changed after creation.** |
| Display Name | `Acme` | Shown in lists and consent screens |
| Description | `Team task manager` | Optional |
| Catalog entries | `todo:read`, `todo:write`, `project:read`, `project:write` (one per line) | `<resource>:<action>` strings — the App's permission vocabulary |

After **Create** the app shows up in the list.

::: tip
Catalog entries aren't carved in stone — you can extend them later.
But: existing roles break if you remove an entry that's still in use.
The admin UI surfaces those references before letting you delete.
:::

Beyond the catalog, an app's **Settings** tab lets you override a slice
of the realm's configuration per app — its own subdomain/origin,
branding, self-registration posture, and native-grant / DCR / CIMD
toggles — while anything left off inherits the realm. See
[Application settings](../admin/applications#application-settings).

## Station 2: OAuth client for the frontend

The OAuth client is the identity your app's **frontend** uses when
requesting tokens from the IDP. An SPA, a mobile app, a desktop tool
— they're all clients.

Navigate to **Administration → OAuth Clients**. Click **Create**. The Create modal exposes the full set of fields — Grant Types, Redirect URIs, Allowed Scopes, Access Token Type, Applications, CORS Origins — so you set everything in one pass; there's no second "edit after create" step required.

| Field | Example | Explanation |
| --- | --- | --- |
| Client ID | `acme-web` | Stable identifier used in the OAuth flow |
| Display Name | `Acme Web` | UI label |
| Client type | `confidential` | `confidential` for server-side / backend (BFF) clients, `public` for browser-only SPA / mobile |
| Consent type | `implicit` | for trusted first-party apps; `explicit` shows a consent screen |
| **Applications** | pick `acme` | **Important** — binds the client to the app. Multi-select is allowed (multi-app frontends). |
| Client Secret | leave empty = generate | Auto-generated for `confidential`, **shown only once** — copy it! |
| Redirect URIs | `https://acme.dev.local/auth/callback` | One per line |
| Post-Logout Redirect URIs | `https://acme.dev.local/` | One per line |
| Allowed Grant Types | `authorization_code` + `refresh_token` | For a web app pick `authorization_code` and `refresh_token`. There are no silent defaults — a client with no grant types cannot mint tokens. |
| Allowed Scopes | `openid email profile roles permissions acme` | The OIDC scopes plus the resource-bearing `acme` scope you create in Station 3. Request `roles` to get the per-audience role list, `permissions` for the `<resource>:<action>` list. |
| **Access Token Type** | `JWT` | **Required for local JWKS validation.** The default is `Reference` (opaque — the resource server would have to call `/connect/introspect` on every request). `AddJwtBearer` validates JWTs by signature, so choose **JWT** here. |

Click **Create**. The client secret is shown — copy it and store it
safely; you'll never see it again.

::: tip Browser-only SPA (PKCE, no backend)
If your frontend is a pure SPA that talks to the IDP directly (PKCE, no server-side BFF), set **Client type** to `public`, leave the secret empty, and add the SPA's origin (e.g. `https://acme.dev.local`) to the client's **Allowed CORS Origins**. The OIDC endpoints (`/connect/authorize`, `/connect/token`, `/connect/userinfo`) only echo CORS headers for origins registered on a client in the active realm, so a missing origin makes the browser block the cross-origin call. A confidential / BFF web app (the primary path above) makes its token calls server-side and doesn't need a CORS origin.
:::

::: info What does the apps choice change?
On `/connect/userinfo` the access token's principal gets a
`resource_access` block per linked app, with the user's app-specific
roles (with `scope=roles`) and bypass-pre-expanded permissions narrowed
to the calling OAuthApi's `PermissionIds` (with `scope=permissions`).
The client may also only request scopes that belong to one of its apps
(plus the standard OIDC scopes).
:::

## Station 3: create the resource server

The resource server is the identity Modgud uses to compute the
per-Audience subset narrowing in `resource_access` UserInfo blocks.
Each App needs at least one.

Go to **Administration → OAuth → APIs** and click **Create**:

- **Name** — `acme` (this becomes the `aud` claim)
- **Application** — pick the `acme` App you just registered
- **PermissionIds** — leave full catalog for now (a microservice would
  tighten this to its slice)

Save. The OAuth API now exists and the IdP knows which catalog to
resolve against when a token targets `aud=acme`.

### 3a. Create the resource-bearing scope (don't skip this)

The six default scopes seeded into every realm (`openid`, `email`, `profile`, `roles`, `permissions`, `offline_access`) all have **empty Resources**. A token only gets `aud=acme` when one of the **requested** scopes carries `Resources=[acme]` — and without that audience the IdP never emits a `resource_access[acme]` block, so your resource server's audience check 401s. The default scopes alone are not enough.

On the API's detail view click **Create implicit scope** (this calls `POST /api/admin/oauth/apis/{id}/create-implicit-scope`). It mints a scope named `acme` with `Resources=[acme]`, hidden from the discovery document by default (clients learn their scopes from your docs, not from `.well-known`). This is the scope that puts `aud=acme` on the token.

Then make sure the `acme` scope is actually requested end-to-end:

1. Add `acme` to the client's **Allowed Scopes** (Station 2 — it's already in the example list above).
2. Include `acme` in the `scope` parameter of the authorize request, e.g. `scope=openid email profile roles permissions acme`.

Only then does the access token carry `aud=acme` and the principal a `resource_access[acme]` block. Inside that block, the `roles` array appears only if the request also included the `roles` scope, and the `permissions` array only if it included the `permissions` scope.

::: tip Microservice apps
Multi-service apps create one OAuthApi per microservice, each with a
narrower `PermissionIds` subset (and its own implicit scope). The user's
`resource_access[<service>]` block for that specific microservice is
then narrowed to its declared subset — sibling microservices'
permissions don't leak.
:::

## Station 4: roles and groups

On setup Modgud seeds exactly one realm admin (`Administrators`
group with wildcard `BoundTo: ["*"]`). For your new app you'll
usually want more nuanced roles.

### 4a. Create a role

**Administration → Roles → Create**.

| Field | Example |
| --- | --- |
| Name | `Acme Editor` |
| Description | `May create and edit todos and projects` |
| **App** | `acme` |
| Permissions | `todo:read`, `todo:write` |

Roles bind to one App via `AppId`; the `PermissionIds` reference
specific catalog entries of that App. The same string `todo:read`
in a different App's catalog is a different permission.

### 4b. Create a group

**Administration → Groups → Create**.

| Tab | Field | Example |
| --- | --- | --- |
| General | Name | `Acme Team` |
| General | **Bound to apps** | pick `acme` |
| Members | (user list) | yourself + colleagues |
| Roles |  | `Acme Editor` |

::: warning BoundTo matters
A group only takes effect in the apps listed in BoundTo. Pick
**★ All apps (\*)** only for realm-wide admin groups. Leave it empty
for pure mailing-list / org-only groups.
:::

Save. Users in this group now hold `todo:read` + `todo:write` within
the `acme` app context.

## Station 5: resource-server code

Now the backend configuration of your SaaS app. ASP.NET Core example:

A complete, runnable version of everything below ships in the repo at `src/dotnet/TestApps/Modgud.TestApps.ResourceApi/Program.cs` — that's the canonical example the integration tests run against. The snippets here are trimmed for the walkthrough.

### Packages

```bash
dotnet add package Modgud.Client.AspNetCore
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
```

### `Program.cs`

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Modgud.Client.AspNetCore;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Authority is the realm's HOST ROOT — realms resolve by Host
        // header, so the issuer has NO realm path. Never append "/system"
        // or any "/<realm>" segment: a path-suffixed Authority makes the
        // discovery fetch 404 and fails issuer validation.
        options.Authority = "https://auth.example.com";
        options.Audience  = "acme";   // matches the OAuthApi name / aud claim
    });

// AddModgudClient does the /connect/userinfo round-trip (hooks
// JwtBearerEvents.OnTokenValidated) and registers the ClaimsTransformation
// that flattens resource_access["acme"] onto the principal:
//   - resource_access["acme"].roles       → ClaimTypes.Role
//   - resource_access["acme"].permissions → "permission" claims
// The IdP pre-expands bypass tiers (realm:admin, <resource>:admin) before
// emission, so the RS only ever does exact-match — no evaluator on this side.
// Do NOT set GetClaimsFromUserInfoEndpoint on AddJwtBearer; AddModgudClient
// owns the UserInfo fetch.
builder.Services.AddModgudClient(o =>
{
    o.Authority = "https://auth.example.com";
    o.Audience  = "acme";   // must equal JwtBearerOptions.Audience above
});

builder.Services.AddAuthorization();
```

### Coarse role check

```csharp
app.MapGet("/admin", () => "Admin only")
   .RequireAuthorization(p => p.RequireRole("Acme Editor"));
```

`[Authorize(Roles = "Acme Editor")]` works the same way — the
transformation surfaces `resource_access["acme"].roles` as
`ClaimTypes.Role` claims.

### Granular permission check

Gate endpoints with `.RequiresModgudPermission(...)` — the filter reads
the flattened `permission` claims and does a straight exact-match:

```csharp
app.MapPost("/todos", () => Results.Ok())
   .RequireAuthorization()
   .RequiresModgudPermission("todo:write");
```

If you need to read permissions imperatively, they live under
`ModgudClaimsTransformation.PermissionClaimType`:

```csharp
app.MapGet("/whoami", (ClaimsPrincipal user) => Results.Ok(new
{
    permissions = user
        .FindAll(ModgudClaimsTransformation.PermissionClaimType)
        .Select(c => c.Value),
})).RequireAuthorization();
```

Full integration patterns (authorization policies, dynamic checks,
common pitfalls) live in
[Guide → Integrating a Resource Server](../integrate/resource-server).

## End-to-end test

1. Open `https://acme.dev.local`
2. The frontend redirects you to the Modgud login page with `scope=openid email profile roles permissions acme` (the `acme` scope is what puts `aud=acme` on the token)
3. Log in as a user from station 4
4. Consent screen (if `explicit` consent type)
5. Redirect back to the app with an auth code
6. The app exchanges the code at `/connect/token`
7. The app calls `/connect/userinfo`, sees `sub`, `email`, `name`,
   and `resource_access.acme.roles = ["Acme Editor"]` plus
   `resource_access.acme.permissions = ["todo:read", "todo:write"]`
8. `[Authorize(Roles = "Acme Editor")]` lets you in, and
   `.RequiresModgudPermission("todo:write")` passes — the resource
   server validated the JWT against the realm's JWKS (because the client's
   Access Token Type is JWT) and matched the flattened `permission` claims

Made it through? **Done. First SaaS app integrated.**

## What comes next

- **Multiple apps in one client:** a frontend that bundles two apps
  assigns its OAuth client to both. The user's UserInfo response then
  carries a `resource_access[<a>]` block and a
  `resource_access[<b>]` block. Each backend reads its own block.
- **Microservice apps:** several resource servers under one app —
  create more OAuth APIs in the **OAuth APIs** admin and link them
  all to the same App, each with its own narrower `PermissionIds`
  subset.
- **External login providers:** under
  [Login Providers](./login-providers) you configure Google /
  Microsoft / EntraID. Modgud stays the central IDP but delegates
  the login step.
- **Standing up a second, similar app:** right-click an existing App,
  Client, Scope, API, Role, or Group in its list and choose **Clone**
  to pre-fill a new one from it, instead of repeating all five
  stations from scratch. See [Cloning an app](../admin/applications#cloning-an-app).

## Tips and pitfalls

- **Permission strings have two segments:** `<resource>:<action>`,
  inside an App's catalog. The App context is implicit from the
  catalog — the same string in two different App catalogs is two
  different permissions.
- **`BoundTo: []` ≠ `BoundTo: ["*"]`.** Empty = the group is dormant
  for permission purposes but can still be used for mailing-list.
  Wildcard = active everywhere.
- **Don't try to delete the system app `modgud`.** It's flagged
  `IsSystem`; the attempt is rejected.
- **Lost realm admin.** If you locked yourself out of the
  `Administrators` group: the recovery CLI inside the container can
  pull you back in — see [Recovery CLI](../operate/recovery-cli).
- **Lost a secret.** Client secrets are shown exactly once. If you've
  lost one: **regenerate** in the corresponding detail modal.
- **No `aud=acme`, no `resource_access`.** The default scopes carry
  empty Resources, so the token gets no audience and the block is never
  emitted (your RS then 401s on its audience check). Create the API's
  implicit scope (Station 3a) and request `acme` in the authorize
  request.
- **`scope=permissions` not requested.** Without it, the
  `permissions` array in the `resource_access` block is omitted — your
  `RequiresModgudPermission(…)` check sees nothing. Same for `roles`
  and the role list. Add the scope to the client's allowed-scopes list
  and to every authorization request.
- **Access Token Type left as Reference.** `AddJwtBearer` can only
  validate JWTs locally. A Reference (opaque) token has nothing to
  validate by signature — switch the client to **JWT** (Station 2).
- **Authority has a realm path.** `Authority` must be the host root
  (`https://auth.example.com`), never `…/system` or `…/<realm>`. Realms
  resolve by Host header; a path-suffixed Authority breaks discovery and
  issuer validation.
