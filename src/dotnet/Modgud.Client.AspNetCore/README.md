# Modgud.Client.AspNetCore

ASP.NET Core integration for resource servers that authenticate against a
[Modgud](https://github.com/cocoar-dev/modgud) identity provider.

The lib does two things on top of vanilla `AddJwtBearer`:

1. Fetches `{Authority}/connect/userinfo` on token validation and merges
   the `resource_access[<audience>]` block onto the principal.
2. Flattens that block into native `ClaimTypes.Role` / `"permission"` /
   `"group"` claims so `[Authorize(Roles = "...")]` and an
   `.RequiresCocoarPermission("...")` endpoint filter work natively.

Bypass tiers (`realm:admin`, `<resource>:admin`) are pre-expanded
**IdP-side** before emission, so the client lib does pure
exact-match — no evaluator logic, no HTTP client, no caching.

## Install

```bash
dotnet add package Modgud.Client.AspNetCore
```

## Quickstart

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
   .RequiresCocoarPermission("calendar:write");

app.Run();
```

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

| Option | Description |
| --- | --- |
| `Authority` | IdP base URL. Used to fetch `{Authority}/connect/userinfo`. Same value as `JwtBearerOptions.Authority`. |
| `Audience` | The audience this resource server identifies as — same value as `JwtBearerOptions.Audience`. Looked up against `resource_access[…]`. |
| `JwtBearerScheme` | Scheme name to attach to. Defaults to `"Bearer"`. |

## License

Apache-2.0. See [LICENSE](https://github.com/cocoar-dev/modgud/blob/develop/LICENSE).
