# SaaS App Integration Walkthrough

This page takes you from a freshly installed Cocoar.Auth all the way to a working external app (e.g. TimeToDo) doing single-sign-on against Cocoar.Auth and looking up live permissions through the distribution API.

> **Audience:** realm admins and developers integrating their own Cocoar SaaS app. Regular end-user onboarding is documented in [first steps](../end-user/first-steps).

## Conceptual overview

Cocoar.Auth models the world in three layers:

- **Realm** — a tenant. Own database, own users, own apps. Setup automatically creates the `system` realm.
- **App** — a SaaS application within a realm (e.g. `cocoar-auth`, `timetodo`, `knowledge`). Each app owns its resources, roles, and links to zero or more OAuth clients and resource servers.
- **Group / Role / Permission** — who may do what in which app. Groups bundle users, roles bundle permissions, permissions are `app:resource:action` strings.

When you bind a new SaaS app you traverse **five stations**:

1. Register the app
2. Create an OAuth client for the app's frontend
3. Provision the default resource server (one click)
4. Optional: create roles + assign to a group
5. Configure the resource-server code in the SaaS app's backend

## Prerequisites

You need:

- A running Cocoar.Auth instance (see [Getting Started](../getting-started/quickstart))
- An admin account (a member of the `Administratoren` group, seeded on first `/setup`)
- A URL for your target app (for redirect URIs), e.g. `https://timetodo.dev.local`

## Station 1: register the app

Navigate to **Administration → Applications** (sidebar entry under "Apps"). You'll see at least the system app `cocoar-auth`.

Click **Create**.

| Field | Example | Explanation |
| --- | --- | --- |
| Slug (immutable) | `timetodo` | Permission prefix, kebab-case. **Cannot be changed after creation.** |
| Display Name | `TimeToDo` | Shown in lists and consent screens |
| Description | `Team task manager` | Optional |
| Resources | `todo`, `project`, `tag` (one per line) | The business objects the app manages — these become permissions like `timetodo:todo:read` |

After **Create** the app shows up in the list.

::: tip
Resources aren't carved in stone — you can extend them later. But: existing roles/permissions break if you remove a resource that's still in use.
:::

## Station 2: OAuth client for the frontend

The OAuth client is the identity your app's **frontend** uses when requesting tokens from the IDP. An SPA, a mobile app, a desktop tool — they're all clients.

Navigate to **Administration → OAuth Clients**. Click **Create**.

| Field | Example | Explanation |
| --- | --- | --- |
| Client ID | `timetodo-web` | Stable identifier used in the OAuth flow |
| Display Name | `TimeToDo Web` | UI label |
| Client type | `confidential` | `confidential` for server-side / backend clients, `public` for SPA / mobile |
| Consent type | `implicit` | for trusted first-party apps; `explicit` shows a consent screen |
| **Applications** | pick `timetodo` | **Important** — binds the client to the app. Multi-select is allowed (multi-app frontends). Empty = realm-wide. |
| Client Secret | leave empty = generate | Auto-generated for `confidential`, **shown only once** — copy it! |
| Redirect URIs | `https://timetodo.dev.local/auth/callback` | One per line |
| Post-Logout Redirect URIs | `https://timetodo.dev.local/` | One per line |
| Allowed Grant Types | `authorization_code, refresh_token` | Comma-separated |

Click **Create**. The client secret is shown — copy it and store it safely; you'll never see it again.

::: info What does the apps choice change?
On `/connect/userinfo` the issued access token gets a `resource_access` block per linked app, with the user's app-specific roles. The client may also only request scopes that belong to one of its apps (or the global OIDC standard scopes).
:::

## Station 3: provision the default resource server

The resource server is the identity your app's **backend** uses to identify itself to Cocoar.Auth when looking up permissions live through the distribution API. It's a different identity from the OAuth client.

Go back to **Administration → Applications**, open your `timetodo` app by double-clicking.

At the bottom of the modal you'll see a **Resource Server** section with a **Create default resource server** button.

Click it. A yellow note appears with the **API secret** — that's the resource-server counterpart to the client secret. **Copy and store it safely** (e.g. drop it into TimeToDo's configuration); you'll never see it again.

What happens internally:
- A new OAuth API named `timetodo` is created
- It is linked to the `timetodo` app (`AppId`)
- An initial API secret is returned

If you press the button again later: Cocoar.Auth detects an existing default RS and just shows "Already exists" — no new secret.

::: tip Do I really need this?
Only if your backend wants to look up granular permissions (`timetodo:todo:write`) live. If your app only checks coarse roles (`Admin`, `Viewer`), the OAuth client + UserInfo are enough; skip this station.
:::

## Station 4: roles and groups

On setup Cocoar.Auth seeds exactly one realm admin (`Administratoren` group with wildcard `BoundTo: ["*"]`). For your new app you'll usually want more nuanced roles.

### 4a. Create a role

**Administration → Roles → Create**.

| Field | Example |
| --- | --- |
| Name | `TimeToDo Editor` |
| Description | `May create and edit todos and projects` |
| **AppSlug** | `timetodo` |
| Resource Type | `todo` |
| Permissions | `read`, `write` |

→ Effectively granted: `timetodo:todo:read`, `timetodo:todo:write`.

Need a role that spans several resources? Leave `Resource Type` empty and put the permissions fully-qualified into the list:

```
timetodo:todo:read
timetodo:todo:write
timetodo:project:read
timetodo:project:write
```

### 4b. Create a group

**Administration → Groups → Create**.

| Tab | Field | Example |
| --- | --- | --- |
| General | Name | `TimeToDo Team` |
| General | **Bound to apps** | pick `timetodo` |
| Members | (user list) | yourself + colleagues |
| Roles |  | `TimeToDo Editor` |

::: warning BoundTo matters
A group only takes effect in the apps listed in BoundTo. Pick **★ All apps (\*)** only for realm-wide admin groups. Leave it empty for pure mailing-list / org-only groups.
:::

Save. Users in this group now hold `timetodo:todo:read` + `timetodo:todo:write`.

## Station 5: resource-server code

Now the backend configuration of your SaaS app. ASP.NET Core example:

### Packages

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
# Until the NuGet ships, reference the project:
dotnet add reference ../cocoar.auth/src/dotnet/Cocoar.Auth.Client.AspNetCore/Cocoar.Auth.Client.AspNetCore.csproj
```

### `Program.cs`

```csharp
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Your realm issuer — adjust the host + realm slug to your instance.
        options.Authority = "https://auth.cocoar.dev/system";
        options.Audience  = "timetodo";
        options.GetClaimsFromUserInfoEndpoint = true;
    });

// Reads resource_access["timetodo"].roles from the UserInfo claims and
// flattens them into ClaimTypes.Role. With this in place,
// [Authorize(Roles = "TimeToDo Editor")] just works.
services.AddCocoarAuthClaimsTransformation(o =>
{
    o.AppSlug = "timetodo";
});

services.AddAuthorization();
```

### Endpoint example

```csharp
app.MapGet("/admin", () => "Admin only")
   .RequireAuthorization()
   .RequireAuthorization(p => p.RequireRole("TimeToDo Editor"));
```

### Granular: live permission lookups (optional)

If you want to check permissions at the action level (`timetodo:todo:write`), call the distribution API:

```
GET https://auth.cocoar.dev/api/v1/distribution/me-permissions
Authorization: Bearer <user-access-token>
X-Resource-Server-Id: timetodo
X-Resource-Server-Secret: <the-secret-copied-in-station-3>
```

Response:
```json
{
  "UserId": "...",
  "AppSlug": "timetodo",
  "Permissions": ["timetodo:todo:read", "timetodo:todo:write"],
  "Groups": [{ "Id": "...", "Name": "TimeToDo Team" }],
  "Roles":  [{ "Id": "...", "Name": "TimeToDo Editor" }]
}
```

Cache header: `Cache-Control: private, max-age=30` — that means you may cache per user for 30 s, then refresh. Permission revocation propagates within ~30 s.

For a complete code recipe (caching wrapper, policy handlers): see [Integrating a Resource Server](../guide/integrating-resource-server).

## End-to-end test

1. Open `https://timetodo.dev.local`
2. TimeToDo redirects you to the Cocoar.Auth login page
3. Log in as a user from station 4
4. Consent screen (if `explicit` consent type)
5. Redirect back to TimeToDo with auth code
6. TimeToDo exchanges the code at `/connect/token`
7. TimeToDo calls `/connect/userinfo`, sees `sub`, `email`, `name`, and `resource_access.timetodo.roles = ["TimeToDo Editor"]`
8. `[Authorize(Roles = "TimeToDo Editor")]` lets you in

Made it through? **Done. First SaaS app integrated.**

## What comes next

- **Multiple apps in one client:** a frontend that bundles TimeToDo + Knowledge assigns its OAuth client to both apps. The token then carries `resource_access.timetodo.roles` AND `resource_access.knowledge.roles`. Each backend reads its own block.
- **Microservice apps:** several resource servers under one app — create more OAuth APIs in the **OAuth APIs** admin and link them all to the same App.
- **External login providers:** under [Login Providers](./login-providers) you configure Google / Microsoft / EntraID. Cocoar.Auth stays the central IDP but delegates the login step.

## Tips and pitfalls

- **Permission strings have three segments:** `app:resource:action`, not `resource:action`. Every permission since the App model follows this form. Exceptions: `realm:admin` (realm-wide bypass) and `<app>:admin` (app-wide bypass).
- **`BoundTo: []` ≠ `BoundTo: ["*"]`.** Empty = the group is dormant for permission purposes but can still be used for email/mailing-list. Wildcard = active everywhere.
- **Don't delete the system app `cocoar-auth`.** It's flagged IsSystem; the attempt is rejected.
- **Lost realm admin.** If you locked yourself out of the `Administratoren` group: the recovery CLI inside the container can pull you back in — see [Recovery CLI](./recovery-cli).
- **Lost a secret.** Client secrets and API secrets are shown exactly once. If you've lost one: **regenerate** in the corresponding detail modal.
