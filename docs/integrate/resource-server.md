# Integrating a Resource Server

`Modgud.AspNetCore.ResourceServer` protects ASP.NET Core APIs with access
tokens issued by Modgud. One registration method configures one public
authentication scheme for self-contained JWTs, opaque reference tokens, or
both.

All modes:

- validate issuer and audience;
- select the configured `resource_access[<audience>]` block;
- project roles to `ClaimTypes.Role`;
- project permissions to `ModgudClaimTypes.Permission`;
- support `RequireModgudPermission("<resource>:<action>")`.

A runnable sample lives at
`src/dotnet/TestApps/Modgud.TestApps.ResourceApi/Program.cs`. It uses JWT by
default. Set `TESTAPPS:TOKENMODE=reference` or `both` and provide
`TESTAPPS:INTROSPECTIONSECRET=<secret>` for the other modes.

## Admin prerequisites

For the example audience `acme`:

1. Create the app `acme` and its permission catalog, such as `todo:read` and
   `todo:write`.
2. Create an OAuth API named `acme`, link it to the app, and select the
   permissions this API may receive.
3. Allow the OAuth client to request the API's implicit scope plus `roles` and
   `permissions`.
4. Assign roles or permissions to the user through groups bound to the app.

The authorization request must include the relevant scopes:

- the API scope adds `aud=acme`;
- `roles` adds `resource_access[acme].roles`;
- `permissions` adds `resource_access[acme].permissions`.

## Realm authority

Modgud resolves realms by host name, not by a URL path. `Authority` must be the
realm's host root:

- correct: `https://auth.example.com`
- wrong: `https://auth.example.com/system`

Discovery, JWKS, token, UserInfo, and introspection endpoints all live below
that host root.

## Install

```bash
dotnet add package Modgud.AspNetCore.ResourceServer
```

## JWT mode

JWT is the recommended quickstart: validation is local and does not add an IdP
round-trip to each API request. Configure the issuing OAuth client to use
**JWT (self-contained)** access tokens. `OnlyJwt` is the default token mode.

```csharp
using System.Security.Claims;
using Modgud.AspNetCore.ResourceServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "acme";
    options.ConfigureJwtBearer = jwt =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters.NameClaimType = "name";
        jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
    };
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/me", (ClaimsPrincipal user) => new
{
    sub = user.FindFirstValue("sub"),
    name = user.Identity?.Name,
    roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value),
    permissions = user.FindAll(ModgudClaimTypes.Permission)
        .Select(claim => claim.Value),
}).RequireAuthorization();

app.MapGet("/todos", () => Results.Ok(new[] { "buy milk" }))
    .RequireModgudPermission("todo:read");

app.MapPost("/todos", () => Results.Ok())
    .RequireModgudPermission("todo:write");

app.Run();
```

The package validates the token and projects only its embedded
`resource_access` claim. There is no global `IClaimsTransformation` and no
UserInfo fallback. A JWT without the required authorization data may still
authenticate, but role and permission gates remain fail-closed.

JWT authorization data reflects token issuance time. Grant changes become
visible when a new token is issued; revocation is bounded by the access-token
lifetime.

## Reference-token mode

Reference tokens are useful when revocation must take effect immediately. The
OAuth client may remain on Modgud's **Reference** access-token type.

```csharp
builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "acme";
    options.TokenMode = ModgudTokenMode.OnlyReferenceToken;
    options.IntrospectionClientSecret =
        builder.Configuration["Modgud:IntrospectionSecret"];
});
```

Every authenticated request calls `/connect/introspect`. The single response
validates the token and carries the same audience-specific `resource_access`
block as a JWT. Responses are not cached. An inactive token, a failed response,
invalid JSON, or an unreachable IdP rejects authentication.

### Register the introspection client

Create a confidential OAuth client for the resource server:

1. Set its client ID to the resource-server audience, for example `acme`.
2. Generate a client secret.
3. Put that secret in protected application configuration.

`IntrospectionClientId` defaults to `Audience`. Modgud returns an active
introspection result only to the token's presenter or one of its audiences,
which is why the normal resource-server client ID equals the audience.

## Accept both formats

One API can accept both token formats through the same registration and public
authentication scheme:

```csharp
builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "acme";
    options.TokenMode = ModgudTokenMode.Both;
    options.IntrospectionClientSecret =
        builder.Configuration["Modgud:IntrospectionSecret"];
});
```

The package routes signed Modgud JWTs, which consist of exactly three
dot-separated parts, to the JWT validator. Dotless opaque tokens go to
introspection. Routing is not validation: the selected handler still validates
the token completely and fails closed. It never retries a failed JWT through
introspection.

Only one `AddModgudResourceServer(...)` call is allowed for a service
collection. This prevents accidentally registering conflicting Modgud modes.
An application can still intentionally add unrelated ASP.NET Core
authentication schemes alongside Modgud.

## Roles and permissions

The IdP emits a Keycloak-shaped block:

```json
"resource_access": {
  "acme": {
    "roles": ["Acme Editor"],
    "permissions": ["todo:read", "todo:write"]
  }
}
```

Use standard ASP.NET role policies for coarse access:

```csharp
app.MapGet("/admin", () => Results.Ok())
    .RequireAuthorization(policy => policy.RequireRole("Acme Editor"));
```

Use Modgud permission metadata for action-level access:

```csharp
app.MapPost("/todos", () => Results.Ok())
    .RequireModgudPermission("todo:write");
```

The extension works on route handlers and route groups. It requires an
authenticated user and the exact permission claim, returning `401` when
anonymous and `403` when authenticated without the permission.

The IdP expands `realm:admin` and `<resource>:admin` bypass grants into
concrete catalog permissions before emission. It also narrows each audience to
that OAuth API's declared permission subset. Resource servers therefore do
exact matching and do not need `PermissionEvaluator`.

Groups are not emitted across the IdP boundary. Group membership is resolved
to roles and permissions before token issuance.

## DPoP

Both validation paths enforce DPoP binding automatically when a token contains
`cnf.jkt`. A bound token must use the `DPoP` authorization scheme and include a
valid proof whose key and access-token hash match. Replaying it as plain
`Bearer` is rejected. Unbound bearer tokens continue to work normally.

Behind a reverse proxy, configure forwarded headers so the externally visible
scheme and host match the proof's signed `htu`.

## Options and startup validation

| Option | Required | Description |
| --- | --- | --- |
| `Authority` | Always | Realm host root; HTTPS is required by default. |
| `Audience` | Always | Token audience and `resource_access` key. |
| `TokenMode` | No | `OnlyJwt` (default), `OnlyReferenceToken`, or `Both`. |
| `IntrospectionClientId` | No | Defaults to `Audience` in reference-capable modes. |
| `IntrospectionClientSecret` | Reference/Both | Confidential introspection secret. |
| `RequireHttpsMetadata` | No | Set `false` only for local development. |
| `ConfigureJwtBearer` | No | Advanced JWT configuration in JWT-capable modes. |

C# `required` properties cannot express a requirement conditional on
`TokenMode`. The registration therefore validates the complete combination
immediately and throws `OptionsValidationException` for invalid or irrelevant
options.

## Common pitfalls

- A realm path is appended to `Authority`; use the bare realm host root.
- The configured mode does not accept the OAuth client's access-token type.
- The requested API scope did not add the configured audience.
- The authorization request omitted `roles` or `permissions`.
- The OAuth API is not linked to an app or has no selected permissions.
- The introspection client's ID is not the token audience.
- `UseAuthentication()` or `UseAuthorization()` is missing or ordered after
  endpoint execution.

## Reference

- [SaaS App Integration Walkthrough](./saas-walkthrough)
- [Apps and resource_access](../concepts/apps-and-resource-access.md)
- [Permissions and gating](../concepts/permissions.md)
- [OAuth API](../reference/oauth-api.md)
- Source: `src/dotnet/Modgud.AspNetCore.ResourceServer/`
