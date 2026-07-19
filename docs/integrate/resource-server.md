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

### Two token modes — pick one

Modgud issues access tokens in one of two formats, and `Modgud.Client.AspNetCore` supports both. Choose per resource server:

- **JWT (self-contained)** — a signed bearer JWT carrying `aud`, `scope`, and the standard claims, validated locally against the realm's JWKS with no per-request IdP call. **This guide uses JWT.** It requires setting the OAuth client's **Access Token Type** to **JWT (self-contained)** (the field defaults to `Reference` in the client editor).
- **Reference (opaque)** — Modgud's **default** format: an opaque handle with no embedded claims, validated by calling `/connect/introspect` (RFC 7662). No client reconfiguration needed. Wire it with `AddModgudReferenceTokenClient` instead of `AddJwtBearer` + `AddModgudClient` — see [Reference-token mode](#reference-token-mode-opaque-tokens) below.

The endpoint gates (`[Authorize(Roles=…)]`, `RequiresModgudPermission`) and the projected role/permission claims are identical in both modes — only the authentication registration differs.

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

`AddJwtBearer` validates the JWT. `AddModgudClient` adds the two pieces vanilla `AddJwtBearer` lacks: a post-configure on the JwtBearer scheme that makes sure the principal ends up with a `resource_access` claim — preferring the one already embedded in the token and calling `/connect/userinfo` only as a fallback when the token carries none (you do **not** set `GetClaimsFromUserInfoEndpoint`, that property is for `AddOpenIdConnect`) — and a claims transformation that flattens the per-audience block into native role/permission claims, plus the `RequiresModgudPermission` endpoint filter.

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
   .RequiresModgudPermission("todo:read");

app.MapPost("/todos", () => Results.Ok())
   .RequireAuthorization()
   .RequiresModgudPermission("todo:write");

app.Run();
```

`ModgudOptions` has exactly two required properties — `Authority` and `Audience` — plus an optional `JwtBearerScheme` (default `"Bearer"`) if you registered JwtBearer under a custom scheme name. Both `Authority` and `Audience` must match the values you passed to `AddJwtBearer`.

### DPoP-bound tokens are enforced automatically

If a client obtains a [DPoP](../reference/oauth-api#dpop-sender-constrained-tokens)-bound access token (one carrying a `cnf.jkt` confirmation claim), `AddModgudClient` enforces the binding for you — no extra configuration. It accepts the token under the `DPoP` auth scheme (lifting it into JwtBearer, which only reads `Bearer` on its own), then requires a valid DPoP proof whose key matches `cnf.jkt` and whose `ath` hashes the presented token. A bound token replayed as a plain `Bearer`, or with a proof for the wrong key, is rejected. Unbound tokens are unaffected and keep working as bearer tokens. The same enforcement applies on the [reference-token path](#reference-token-mode-opaque-tokens) — the `cnf.jkt` is read from the introspection response instead of the JWT.

Behind a reverse proxy, wire up `UseForwardedHeaders` so the request's scheme + host match what the client signed into the proof's `htu`, or every proof fails the URL check.

## Where permissions come from

`resource_access` is baked directly into the access token at issuance — for JWT clients (the type this guide sets up) it's a claim inside the token itself. `Modgud.Client.AspNetCore` prefers that embedded claim: if the JwtBearer-validated principal already carries `resource_access`, the library reads it as-is and never calls the IdP. It falls back to fetching `/connect/userinfo` only when the token carries no such claim — practically, that's tokens from setups predating this behavior, or resource servers validating opaque reference tokens by some means other than local JWT parsing (this guide's JWKS-based `AddJwtBearer` setup always sees the embedded claim, so the fallback path is dead code in practice for it).

This is a pure performance win, not a freshness trade-off: `/connect/userinfo` has always echoed the exact same `resource_access` block already baked into the token, never a wider or narrower one, so preferring the token claim changes nothing about which permissions your resource server sees — it only removes a redundant HTTP round-trip for tokens that already carry the claim.

| Source | Freshness | IdP dependency per request |
|---|---|---|
| Embedded in token (JWT `resource_access` claim, preferred) | As of token issuance — a grant or revocation takes effect once a new token is minted; propagation is bounded by the access token's lifetime | None |
| `/connect/userinfo` fallback (only when the token carries no `resource_access` claim) | Same as above — UserInfo echoes the token's baked block, it does not recompute a live view | One UserInfo call per request, only for tokens lacking the claim |

## Performance and availability

For tokens that already carry an embedded `resource_access` claim — every JWT-client token, per the prerequisite above — `AddModgudClient` makes **no IdP call at all**: the claims transformation runs purely against data already on the token, so there is no per-request round-trip and nothing to degrade.

The `/connect/userinfo` fallback runs only for tokens without an embedded claim, and the following applies to that path alone. It **degrades without failing the authentication handler** — a `/connect/userinfo` failure never rejects the request outright. But authorization on that path stays **fail-closed**: if the IdP is unreachable or returns a non-2xx, no `resource_access` claim is added, so any endpoint gated with `RequiresModgudPermission` returns `403` (the principal simply carries no permissions) rather than the API 500ing during an IdP outage.

One caveat, still true either way: fail-closed behavior only protects endpoints actually gated on a permission. An endpoint secured with a bare `.RequireAuthorization()` and no `RequiresModgudPermission` call has nothing checking `resource_access` in the first place, so it stays reachable straight through a fallback-path outage. If that matters for a given endpoint, gate it on a permission too.

Because a JWT-client token already carries the claim it needs, an IdP outage no longer 403s requests bearing a still-valid token — those requests never touch the IdP for authorization data in the first place. The fail-closed behavior above only bites setups still on the `/connect/userinfo` fallback path.

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
   .RequiresModgudPermission("todo:write");
```

`RequiresModgudPermission("<resource>:<action>")` is an extension on both `RouteHandlerBuilder` (per-endpoint) and `RouteGroupBuilder` (whole group). It does a straight exact-match against the principal's `"permission"` claims: `401` when anonymous, `403` when authenticated but lacking the permission. The permission string is bare 2-segment (`todo:write`) — the app context is implicit from the audience you configured.

::: tip Roles and permissions compose
The same user can be `Roles = "Editor"` **and** hold `todo:write`. Pick role gates for coarse buckets (`Admin` / `Editor` / `Viewer`) and `RequiresModgudPermission` for per-action checks. Both flavours read from the same `resource_access` block — the token's own embedded copy by default — so there is no separate server-to-server call to wire up.
:::

::: warning Groups are not emitted
The IdP never emits a `groups` block in `resource_access` (hub boundary). Group membership is resolved IdP-side and expanded into roles/permissions before emission. Gate on roles or permissions only — there is no group claim to read.
:::

## What's in the permissions array

The IdP does two transformations before emitting the per-audience block, so your resource server never needs an evaluator:

- **Bypass pre-expansion**: bypass tiers are resolved to concrete catalog strings before emission. `realm:admin` expands to every concrete catalog entry of every reachable app; an `<app>:admin` grant expands to every entry in that app's catalog; a `<resource>:admin` grant expands to every `<resource>:<action>` in the app's catalog. Your check is always exact-match.
- **Per-RS subset narrowing**: each audience block is narrowed to the calling OAuth API's declared `PermissionIds`. A resource server within a multi-RS app sees only its own permissions, never a sibling's.

## Reference-token mode (opaque tokens)

If you'd rather leave the OAuth client on Modgud's default **Reference** token type, validate via introspection instead of JWKS. Everything downstream — the claims transformation, `RequiresModgudPermission`, role gates — is unchanged; only the authentication registration differs:

```csharp
using Modgud.Client.AspNetCore;

builder.Services
    .AddAuthentication(ModgudReferenceTokenDefaults.AuthenticationScheme)
    .AddModgudReferenceTokenClient(o =>
    {
        o.Authority = "https://auth.example.com";   // realm host root
        o.Audience  = "acme";                        // the OAuthApi name == introspection client_id
        o.IntrospectionClientSecret = builder.Configuration["Modgud:IntrospectionSecret"];
    });
```

Each request calls `/connect/introspect`, and the introspection response carries the same per-audience `resource_access` block a JWT would — so a single call both validates the token and yields the permissions. Validation is **fail-closed** (an inactive token, a non-2xx, or an IdP outage rejects the request) and there is **no cache**, so a revoked reference token stops working immediately.

### Setup: register the introspection client

The IdP only reveals a token — its `active` status and its `resource_access` — to a caller that is one of the token's audiences or its presenter. So the resource server introspects with a confidential OAuth client whose **`client_id` equals its audience** (the RS's `OAuthApi` name, which RFC 8707 already puts in the token's `aud`):

1. In Modgud admin, create a **confidential OAuth Client** whose **Client ID** is exactly your audience (e.g. `acme`, or `https://mcp.acme.example` for the MCP case). Give it a secret; it needs no redirect URIs or grant types beyond existing to authenticate.
2. Pass that secret as `IntrospectionClientSecret`. `IntrospectionClientId` defaults to `Audience`, so you don't set it unless the introspection client is registered under a different (still audience-matching) id.

Credentials go in the request body (`client_secret_post`), which also covers a URL-shaped audience id — HTTP Basic would break on the scheme colon.

::: warning A separate introspection identity won't work
A confidential client whose `client_id` is *not* one of the token's audiences gets `active: false` from `/connect/introspect` — the IdP reveals nothing to a stranger. The `client_id == audience` registration above is what makes introspection return an active status and the `resource_access` block.
:::

## Common pitfalls

- **`Authority` has a realm path segment** — e.g. `https://auth.example.com/system`. Discovery fetch 404s and issuer validation fails. Realms route by Host header; `Authority` is the bare host root.
- **Client issues Reference (opaque) tokens** — `AddJwtBearer` cannot validate them. Set the OAuth client's **Access Token Type** to **JWT (self-contained)**.
- **Token's `aud` doesn't match `Audience`** — JWT validation rejects with an audience mismatch. `aud=acme` only appears when a requested scope carries `Resources=[acme]` (the implicit scope from linking the API to the app). Align the API name, the requested scope, and `options.Audience`.
- **`Authority` / `Audience` differ between `AddJwtBearer` and `AddModgudClient`** — UserInfo is fetched from the wrong host or the transformation reads the wrong `resource_access[…]` key, so roles/permissions silently go missing. Keep both pairs identical.
- **`permissions` scope not requested** — `resource_access[acme]` has no `permissions` array, so every `RequiresModgudPermission` gate denies. Add the `permissions` scope to the client's allowed scopes and to the authorization request (same for `roles`).
- **Resource server not linked to an app** — without a linked app there is no `PermissionIds` subset, so the audience block is empty. Open the OAuth API in Modgud admin and assign the app.

## Reference

- Working sample: `src/dotnet/TestApps/Modgud.TestApps.ResourceApi/Program.cs` (+ BFF at `src/dotnet/TestApps/Modgud.TestApps.Bff/Program.cs`)
- Admin walkthrough: [SaaS App Integration Walkthrough](./saas-walkthrough)
- Concept overview: [Apps and resource_access](../concepts/apps-and-resource-access.md)
- Permissions reference: [Permissions & gating](../concepts/permissions.md)
- OAuth endpoints: [reference/oauth-api](../reference/oauth-api.md)
- Library source: `src/dotnet/Modgud.Client.AspNetCore/`
