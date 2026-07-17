# Integrating a Resource Server

This guide walks through wiring an ASP.NET Core resource server to Modgud so it can:

1. Validate access tokens that Modgud issued (JWT signature + issuer + audience, against the realm's JWKS)
2. Pick up role claims so `[Authorize(Roles = "…")]` works
3. Read fine-grained permission strings from the per-audience `resource_access` block so it can gate on `<resource>:<action>` checks

The reference scenario is a fictional `acme` app with a `todo` resource — replace the slugs with yours throughout.

A runnable end-to-end sample lives in the Modgud source tree at `src/dotnet/TestApps/Modgud.TestApps.ResourceApi/Program.cs` (the protected API) and `src/dotnet/TestApps/Modgud.TestApps.Bff/Program.cs` (a cookie-based BFF that obtains and forwards the token). The code below mirrors those samples; when in doubt, read them — they are exercised by the integration test rig.

## Prerequisites

Before wiring code, finish the admin setup in Modgud. The full admin walkthrough lives at [SaaS App Integration Walkthrough](./saas-walkthrough); the essentials are:

1. Create the app `acme` with its permission catalog (`<resource>:<action>` entries such as `todo:read`, `todo:write`)
2. Create an OAuth API (resource server) named `acme` under **OAuth → APIs**, link it to the `acme` app, and pick the catalog subset its `PermissionIds` cover. Linking an API to an app creates an implicit scope whose `Resources` include `acme` — that is what stamps `aud=acme` onto tokens requested with that scope.
3. Create an OAuth client (e.g. `acme-web`) for the app's frontend. Set its **Access Token Type** to **JWT (self-contained)** — see the prerequisite below.
4. Set up at least one role + group with `BoundTo: ["acme"]` and assign your test user.

### Prerequisite: the client must issue JWT access tokens

Modgud's default access-token format is **Reference** (opaque) — an opaque handle the resource server would have to resolve via `/connect/introspect`. The integration in this guide is **JWKS-based**: `AddJwtBearer` validates the token's signature locally and never calls introspection. Opaque reference tokens cannot be validated that way.

So the OAuth client must have its **Access Token Type** set to **JWT (self-contained)** (the **Access Token Type** field in the OAuth client editor, default `Reference`). With JWT selected, the access token is a signed bearer JWT carrying `aud`, `scope`, and the standard claims, which `AddJwtBearer` validates against the realm's JWKS.

### Prerequisite: request the right scopes

The token only carries what was requested:

- `aud=acme` is present only when a requested scope carries `Resources=[acme]` — i.e. the implicit scope created when you linked the `acme` API to the `acme` app (step 2). Without it the token has no `acme` audience and `AddJwtBearer` rejects it with an audience mismatch.
- The `permissions` array inside `resource_access[acme]` appears only when the client requested the `permissions` scope.
- The `roles` array inside `resource_access[acme]` appears only when the client requested the `roles` scope.

Both `roles` and `permissions` are standard scopes seeded into every realm. Add them (plus the API's implicit scope) to the client's allowed scopes and to the authorization request. The [SaaS App Integration Walkthrough](./saas-walkthrough) covers this end to end.

## Host-based realm routing — get the `Authority` right

Modgud resolves realms by the **Host header only**. The issuer carries **no** realm path segment. Each realm answers on its own host (or hostname), and the OIDC discovery document, JWKS, token issuer, and UserInfo endpoint all live at the host root.

That means `Authority` MUST be the realm's **host root**:

- Correct: `https://auth.example.com`
- Wrong: `https://auth.example.com/system` or any `https://auth.example.com/<realm>` path

A path-suffixed authority makes `AddJwtBearer` fetch discovery from `https://auth.example.com/system/.well-known/openid-configuration` (404) and validate the issuer against `https://auth.example.com/system` — both fail. Use the bare host root and let the Host header select the realm.

## ASP.NET Core integration

### 1. Add the package

```bash
dotnet add package Modgud.Client.AspNetCore
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
```

### 2. Configure authentication and the Modgud client

`AddJwtBearer` validates the JWT. `AddModgudClient` adds the two pieces vanilla `AddJwtBearer` lacks: it fetches `/connect/userinfo` and merges the `resource_access` block onto the principal (via a post-configure on the JwtBearer scheme — you do **not** set `GetClaimsFromUserInfoEndpoint`, that property is for `AddOpenIdConnect`), and it registers a claims transformation that flattens the per-audience block into native role/permission claims plus the `RequiresCocoarPermission` endpoint filter.

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Modgud.Client.AspNetCore;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://auth.example.com"; // realm host root — NO realm path segment
        options.Audience  = "acme";                     // matches the OAuthApi name registered in Modgud
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
    });

builder.Services.AddModgudClient(o =>
{
    o.Authority = "https://auth.example.com";  // same as JwtBearer Authority
    o.Audience  = "acme";                      // same as JwtBearer Audience
});

builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/me", (ClaimsPrincipal user) => new
{
    sub = user.FindFirstValue("sub"),
    name = user.Identity?.Name,
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value),
    permissions = user.FindAll(ModgudClaimsTransformation.PermissionClaimType).Select(c => c.Value),
}).RequireAuthorization();

app.MapGet("/todos", () => Results.Ok(new[] { "buy milk" }))
   .RequireAuthorization()
   .RequiresCocoarPermission("todo:read");

app.MapPost("/todos", () => Results.Ok())
   .RequireAuthorization()
   .RequiresCocoarPermission("todo:write");

app.Run();
```

`ModgudOptions` has exactly two required properties — `Authority` and `Audience` — plus an optional `JwtBearerScheme` (default `"Bearer"`) if you registered JwtBearer under a custom scheme name. Both `Authority` and `Audience` must match the values you passed to `AddJwtBearer`.

## Performance and availability

`AddModgudClient` fetches `/connect/userinfo` **once per authenticated request** — there is no response caching yet, so every call into your resource server costs a round-trip to the IdP. Reducing that per-request cost is planned, but there's no firm timeline for it yet.

The enrichment also **fails open**: if the IdP is unreachable or returns a non-2xx, the request proceeds without the `resource_access` claim rather than being rejected outright. In practice this means `RequiresCocoarPermission` gates return `403` (the principal simply carries no permissions) rather than the API 500ing during an IdP outage. Keep access-token lifetimes short so a principal missing its permissions doesn't linger longer than necessary.

## Reading roles and permissions

The claims transformation projects the per-audience block onto flat claims:

- Roles land on `ClaimTypes.Role`, so `[Authorize(Roles = "Editor")]`, `RequireRole(...)`, and `user.FindAll(ClaimTypes.Role)` all work.
- Permissions land on the claim type `ModgudClaimsTransformation.PermissionClaimType` (value `"permission"`). Read them with `user.FindAll(ModgudClaimsTransformation.PermissionClaimType)`.

```csharp
// Coarse role gate.
app.MapGet("/admin/reports", () => Results.Ok())
   .RequireAuthorization(p => p.RequireRole("Editor"));

// Granular permission gate — the canonical way.
app.MapPost("/todos", () => Results.Ok())
   .RequireAuthorization()
   .RequiresCocoarPermission("todo:write");
```

`RequiresCocoarPermission("<resource>:<action>")` is an extension on both `RouteHandlerBuilder` (per-endpoint) and `RouteGroupBuilder` (whole group). It does a straight exact-match against the principal's `"permission"` claims: `401` when anonymous, `403` when authenticated but lacking the permission. The permission string is bare 2-segment (`todo:write`) — the app context is implicit from the audience you configured.

::: tip Roles and permissions compose
The same user can be `Roles = "Editor"` **and** hold `todo:write`. Pick role gates for coarse buckets (`Admin` / `Editor` / `Viewer`) and `RequiresCocoarPermission` for per-action checks. Both flavours read from the same UserInfo-sourced `resource_access` block — there is no separate server-to-server call to wire up.
:::

::: warning Groups are not emitted
The IdP never emits a `groups` block in `resource_access` (hub boundary). Group membership is resolved IdP-side and expanded into roles/permissions before emission. Gate on roles or permissions only — there is no group claim to read.
:::

## What's in the permissions array

The IdP does two transformations before emitting the per-audience block, so your resource server never needs an evaluator:

- **Bypass pre-expansion**: bypass tiers are resolved to concrete catalog strings before emission. `realm:admin` expands to every concrete catalog entry of every reachable app; an `<app>:admin` grant expands to every entry in that app's catalog; a `<resource>:admin` grant expands to every `<resource>:<action>` in the app's catalog. Your check is always exact-match.
- **Per-RS subset narrowing**: each audience block is narrowed to the calling OAuth API's declared `PermissionIds`. A resource server within a multi-RS app sees only its own permissions, never a sibling's.

## Common pitfalls

- **`Authority` has a realm path segment** — e.g. `https://auth.example.com/system`. Discovery fetch 404s and issuer validation fails. Realms route by Host header; `Authority` is the bare host root.
- **Client issues Reference (opaque) tokens** — `AddJwtBearer` cannot validate them. Set the OAuth client's **Access Token Type** to **JWT (self-contained)**.
- **Token's `aud` doesn't match `Audience`** — JWT validation rejects with an audience mismatch. `aud=acme` only appears when a requested scope carries `Resources=[acme]` (the implicit scope from linking the API to the app). Align the API name, the requested scope, and `options.Audience`.
- **`Authority` / `Audience` differ between `AddJwtBearer` and `AddModgudClient`** — UserInfo is fetched from the wrong host or the transformation reads the wrong `resource_access[…]` key, so roles/permissions silently go missing. Keep both pairs identical.
- **`permissions` scope not requested** — `resource_access[acme]` has no `permissions` array, so every `RequiresCocoarPermission` gate denies. Add the `permissions` scope to the client's allowed scopes and to the authorization request (same for `roles`).
- **Resource server not linked to an app** — without a linked app there is no `PermissionIds` subset, so the audience block is empty. Open the OAuth API in Modgud admin and assign the app.

## Reference

- Working sample: `src/dotnet/TestApps/Modgud.TestApps.ResourceApi/Program.cs` (+ BFF at `src/dotnet/TestApps/Modgud.TestApps.Bff/Program.cs`)
- Admin walkthrough: [SaaS App Integration Walkthrough](./saas-walkthrough)
- Concept overview: [Apps and resource_access](../concepts/apps-and-resource-access.md)
- Permissions reference: [Permissions & gating](../concepts/permissions.md)
- OAuth endpoints: [reference/oauth-api](../reference/oauth-api.md)
- Library source: `src/dotnet/Modgud.Client.AspNetCore/`
