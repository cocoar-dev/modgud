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
3. Create the resource server (OAuth API) and link it to the app
4. Optional: create roles + assign to a group
5. Configure the resource-server code in the SaaS app's backend

## Prerequisites

You need:

- A running Modgud instance (see
  [Getting Started](../getting-started/quickstart))
- An admin account (a member of the `Administratoren` group, created
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

## Station 2: OAuth client for the frontend

The OAuth client is the identity your app's **frontend** uses when
requesting tokens from the IDP. An SPA, a mobile app, a desktop tool
— they're all clients.

Navigate to **Administration → OAuth Clients**. Click **Create**.

| Field | Example | Explanation |
| --- | --- | --- |
| Client ID | `acme-web` | Stable identifier used in the OAuth flow |
| Display Name | `Acme Web` | UI label |
| Client type | `confidential` | `confidential` for server-side / backend clients, `public` for SPA / mobile |
| Consent type | `implicit` | for trusted first-party apps; `explicit` shows a consent screen |
| **Applications** | pick `acme` | **Important** — binds the client to the app. Multi-select is allowed (multi-app frontends). |
| Client Secret | leave empty = generate | Auto-generated for `confidential`, **shown only once** — copy it! |
| Redirect URIs | `https://acme.dev.local/auth/callback` | One per line |
| Post-Logout Redirect URIs | `https://acme.dev.local/` | One per line |
| Allowed Grant Types | `authorization_code, refresh_token` | Comma-separated |
| Allowed Scopes | `openid email profile roles permissions` | Request `permissions` if your backend gates on `<resource>:<action>` |

Click **Create**. The client secret is shown — copy it and store it
safely; you'll never see it again.

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

::: tip Microservice apps
Multi-service apps create one OAuthApi per microservice, each with a
narrower `PermissionIds` subset. The user's `resource_access[acme]`
block for that specific microservice is then narrowed to its
declared subset — sibling microservices' permissions don't leak.
:::

## Station 4: roles and groups

On setup Modgud seeds exactly one realm admin (`Administratoren`
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

### Packages

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
# Until the NuGet ships, reference the project:
dotnet add reference ../modgud/src/dotnet/Modgud.Client.AspNetCore/Modgud.Client.AspNetCore.csproj
```

### `Program.cs`

```csharp
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Your realm issuer — adjust the host + realm slug to your instance.
        options.Authority = "https://auth.example.com/system";
        options.Audience  = "acme";
        options.GetClaimsFromUserInfoEndpoint = true;
    });

// Wires the Modgud ClaimsTransformation that reads the per-Audience
// resource_access block and flattens it onto the principal:
//   - resource_access["acme"].roles → ClaimTypes.Role
//     so [Authorize(Roles = "Acme Editor")] just works.
//   - resource_access["acme"].permissions → ModgudClaimTypes.Permission
//     so user.HasClaim(ModgudClaimTypes.Permission, "todo:write") works.
services.AddModgudClient(o =>
{
    o.AppSlug = "acme";
});

services.AddAuthorization();
```

### Coarse role check

```csharp
app.MapGet("/admin", () => "Admin only")
   .RequireAuthorization()
   .RequireAuthorization(p => p.RequireRole("Acme Editor"));
```

### Granular permission check

```csharp
app.MapPost("/todos", (ClaimsPrincipal user, TodoDto dto) =>
{
    if (!user.HasClaim(ModgudClaimTypes.Permission, "todo:write"))
        return Results.Forbid();
    // … create todo
    return Results.Ok();
});
```

Full integration patterns (authorization policies, dynamic checks,
common pitfalls) live in
[Guide → Integrating a Resource Server](../guide/integrating-resource-server).

## End-to-end test

1. Open `https://acme.dev.local`
2. The frontend redirects you to the Modgud login page
3. Log in as a user from station 4
4. Consent screen (if `explicit` consent type)
5. Redirect back to the app with an auth code
6. The app exchanges the code at `/connect/token`
7. The app calls `/connect/userinfo`, sees `sub`, `email`, `name`,
   and `resource_access.acme.roles = ["Acme Editor"]` plus
   `resource_access.acme.permissions = ["todo:read", "todo:write"]`
8. `[Authorize(Roles = "Acme Editor")]` lets you in;
   `user.HasClaim(ModgudClaimTypes.Permission, "todo:write")` returns
   true

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
  `Administratoren` group: the recovery CLI inside the container can
  pull you back in — see [Recovery CLI](./recovery-cli).
- **Lost a secret.** Client secrets are shown exactly once. If you've
  lost one: **regenerate** in the corresponding detail modal.
- **`scope=permissions` not requested.** Without it, the
  `permissions` array in the UserInfo `resource_access` block is
  omitted — your `HasClaim(…)` check sees nothing. Add the scope to
  the client's allowed-scopes list and to every authorization
  request.
