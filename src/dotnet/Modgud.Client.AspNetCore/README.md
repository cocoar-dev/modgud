# Modgud.Client.AspNetCore

ASP.NET Core integration for resource servers that authenticate against a
[Modgud](https://github.com/cocoar-dev/modgud) identity provider.

Whichever token format your OAuth client issues, the lib flattens the
per-audience `resource_access[<audience>]` block into native
`ClaimTypes.Role` / `"permission"` claims so `[Authorize(Roles = "...")]`
and an `.RequiresModgudPermission("...")` endpoint filter work natively.
Bypass tiers (`realm:admin`, `<resource>:admin`) are pre-expanded
**IdP-side** before emission, so the lib does pure exact-match — no
evaluator logic.

It supports both Modgud access-token formats:

- **JWT access tokens** — `AddModgudClient` on top of `AddJwtBearer`.
  JwtBearer validates the token locally against the realm JWKS; the lib
  reads `resource_access` from the token itself, falling back to
  `{Authority}/connect/userinfo` only for tokens that don't carry it.
- **Reference (opaque) access tokens** — `AddModgudReferenceTokenClient`.
  This is Modgud's **default** token format. Each request validates the
  token via `{Authority}/connect/introspect` (RFC 7662) and reads
  `resource_access` from the introspection response — one call, no
  separate UserInfo round-trip. Validation is fail-closed and there is no
  cache, so revocation is instant.

## Install

```bash
dotnet add package Modgud.Client.AspNetCore
```

## Quickstart — JWT access tokens

Requires the OAuth client's **Access Token Type** to be **JWT (self-contained)**.

```csharp
using Modgud.Client.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://auth.example.com";
        options.Audience  = "event-tree-api";   // matches an OAuthApi in the IdP
    });

builder.Services.AddModgudClient(o =>
{
    o.Authority = "https://auth.example.com";
    o.Audience  = "event-tree-api";             // same value as above
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Role-gated — uses the standard [Authorize] attribute since
// roles are projected to ClaimTypes.Role.
app.MapGet("/admin/ping", () => "pong")
   .RequireAuthorization(p => p.RequireRole("Editor"));

// Permission-gated — bare 2-segment string. The IdP has already
// expanded realm:admin / <resource>:admin to catalog entries, so
// this is a pure contains-check.
app.MapPost("/calendars/{id}", (string id) => Results.Ok())
   .RequiresModgudPermission("calendar:write");

app.Run();
```

## Quickstart — reference (opaque) access tokens

Works with Modgud's **default** token format — no need to switch the client
to JWT. The endpoint gates (`RequireRole`, `RequiresModgudPermission`) are
identical to the JWT quickstart; only the authentication registration differs:

```csharp
using Modgud.Client.AspNetCore;

builder.Services
    .AddAuthentication(ModgudReferenceTokenDefaults.AuthenticationScheme)
    .AddModgudReferenceTokenClient(o =>
    {
        o.Authority = "https://auth.example.com";
        o.Audience  = "event-tree-api";   // == the introspection client_id
        o.IntrospectionClientSecret = builder.Configuration["Modgud:IntrospectionSecret"];
    });
```

**Setup requirement.** The resource server introspects with a confidential
OAuth client whose **`client_id` equals its `Audience`**. The IdP only
reveals a token — its `active` status and `resource_access` block — to a
caller that is one of the token's audiences (or its presenter); the audience
is the RS's own id (the RFC 8707 `resource=` value), so registering the
introspection client under that same id is what authorises it. Credentials
are sent as form-body parameters (`client_secret_post`), which also handles a
URL-shaped audience id that HTTP Basic can't (it splits on the scheme colon).

## How the claims land on the principal

The IdP emits permissions per audience in Keycloak shape:

```json
"resource_access": {
  "event-tree-api": {
    "roles":       ["Editor", "Viewer"],
    "permissions": ["calendar:read", "calendar:write"]
  }
}
```

`ModgudClaimsTransformation` projects that into flat claims:

| Source field | Flat claim type |
| --- | --- |
| `roles` | `ClaimTypes.Role` |
| `permissions` | `"permission"` |

> Groups are deliberately **not** emitted by the IdP (hub boundary): group
> membership is IdP-internal and is expanded into roles/permissions before
> emission. The `GroupClaimType` constant and the old `groups` flattener are
> retained only for binary compatibility and are `[Obsolete]`.

Read them with standard claims APIs:

```csharp
var perms = ctx.User.FindAll("permission").Select(c => c.Value);
```

## Configuration reference

`AddModgudClient` (JWT mode) — `ModgudOptions`:

| Option | Description |
| --- | --- |
| `Authority` | IdP base URL. Used to fetch `{Authority}/connect/userinfo`. Same value as `JwtBearerOptions.Authority`. |
| `Audience` | The audience this resource server identifies as — same value as `JwtBearerOptions.Audience`. Looked up against `resource_access[…]`. |
| `JwtBearerScheme` | Scheme name to attach to. Defaults to `"Bearer"`. |

`AddModgudReferenceTokenClient` (introspection mode) — `ModgudReferenceTokenOptions`:

| Option | Description |
| --- | --- |
| `Authority` | IdP base URL. Used to build `{Authority}/connect/introspect`. |
| `Audience` | The RS's audience — the `resource_access[…]` key, and the default introspection `client_id`. |
| `IntrospectionClientSecret` | Secret for the introspection client. Required. |
| `IntrospectionClientId` | Overrides the introspection `client_id`. Defaults to `Audience`. |

## License

Apache-2.0. See [LICENSE](https://github.com/cocoar-dev/modgud/blob/develop/LICENSE).
