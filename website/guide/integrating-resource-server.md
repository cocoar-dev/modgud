# Integrating a Resource Server

This guide walks through wiring an ASP.NET Core resource server to
Modgud so it can:

1. Validate access tokens that Modgud issued
2. Pick up role claims via UserInfo so `[Authorize(Roles = "…")]` works
3. Read fine-grained permission strings from the per-Audience
   `resource_access` block so it can gate on
   `<resource>:<action>` checks

The reference scenario is a fictional `acme` app — replace the slug
with yours throughout.

## Prerequisites

Before wiring code, finish the admin setup in Modgud:

1. Create the app `acme` with its permission catalog
   (`<resource>:<action>` entries)
2. Create an OAuth client (e.g. `acme-web`) and link it to `acme`
3. Click **Create default resource server** in the app detail (this
   provisions an OAuthApi identity whose `PermissionIds` declare the
   subset of the catalog this server gates on)
4. Set up at least one role + group with `BoundTo: ["acme"]` and
   assign your test user

The full admin walkthrough lives at
[Admin → SaaS App Integration Walkthrough](../admin/saas-integration-walkthrough).

## ASP.NET Core integration

### 1. Add the package reference

Until the NuGet package ships, reference the project from the Modgud
source tree:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
  <ProjectReference Include="..\..\modgud\src\dotnet\Modgud.Client.AspNetCore\Modgud.Client.AspNetCore.csproj" />
</ItemGroup>
```

### 2. Configure authentication

```csharp
using Modgud.Client.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Your realm's issuer — the segment after /<host>/ is the realm slug.
        options.Authority = "https://auth.example.com/system";

        // Your app's slug. Modgud uses the app slug as the audience
        // claim, so all microservices under one app share the same `aud`.
        options.Audience = "acme";

        // CRITICAL: pulls UserInfo on every token validation and merges its
        // claims into the principal. Without this, role + resource_access
        // claims sit on the IdP and your endpoints don't see them.
        options.GetClaimsFromUserInfoEndpoint = true;
    });

// Wires the Modgud ClaimsTransformation:
//   - flattens resource_access[<AppSlug>].roles into ClaimTypes.Role
//     so [Authorize(Roles = "Editor")] just works.
//   - flattens resource_access[<AppSlug>].permissions onto an exposed
//     IModgudPrincipal so you can call user.HasPermission("todo:write").
services.AddModgudClient(o =>
{
    o.AppSlug = "acme";
});

services.AddAuthorization();
```

### 3. Use it

```csharp
// Any authenticated user.
app.MapGet("/me", (ClaimsPrincipal user) => new
{
    Sub   = user.FindFirstValue(ClaimTypes.NameIdentifier),
    Email = user.FindFirstValue(ClaimTypes.Email),
    Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value),
}).RequireAuthorization();

// Role-only check (coarse).
app.MapDelete("/admin/wipe", () => Results.Ok())
   .RequireAuthorization(p => p.RequireRole("Editor"));
```

## Granular permission checks

For checks finer than role names, read the permissions array out of
the principal. The ClaimsTransformation exposes it as plain claims
under the canonical permission claim type:

```csharp
public static class PrincipalExtensions
{
    public static bool HasPermission(this ClaimsPrincipal user, string permission)
        => user.HasClaim(ModgudClaimTypes.Permission, permission);
}

app.MapPost("/invoices", (ClaimsPrincipal user, InvoiceDto dto) =>
{
    if (!user.HasPermission("invoice:write"))
        return Results.Forbid();

    // … create invoice
    return Results.Ok();
});
```

### Or: an authorization policy

```csharp
public sealed class HasPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class HasPermissionHandler : AuthorizationHandler<HasPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, HasPermissionRequirement req)
    {
        if (ctx.User.HasClaim(ModgudClaimTypes.Permission, req.Permission))
            ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

services.AddSingleton<IAuthorizationHandler, HasPermissionHandler>();
services.AddAuthorization(o =>
    o.AddPolicy("CanWriteInvoices", p =>
        p.AddRequirements(new HasPermissionRequirement("invoice:write"))));

app.MapPost("/invoices", (InvoiceDto dto) => Results.Ok())
   .RequireAuthorization("CanWriteInvoices");
```

### What's in the permissions array

The IdP server-side does two transformations before emitting:

- **Bypass-pre-expansion**: if the user holds `realm:admin`, the
  block lists every concrete catalog entry of every reachable App; a
  `<r>:admin` grant is expanded into every `<r>:*` entry in the
  current App's catalog. Your check is always exact-match — no
  evaluator port required.
- **Per-RS subset narrowing**: each Audience block is narrowed to
  the calling OAuthApi's declared `PermissionIds`. A microservice
  within a multi-RS App sees only its own permissions, never a
  sibling's.

## Two gating flavours, when to pick which

| You need | Use |
| --- | --- |
| Coarse role gating (`Admin`, `Editor`, `Viewer`) | `[Authorize(Roles = "…")]` via UserInfo + claims-transformation. |
| Granular per-action permissions (`invoice:write`, `report:export`) | `user.HasPermission(…)` against the per-Audience `resource_access.permissions` block. |
| Both | Both — they compose. The same user can be `Roles="Editor"` *and* hold permission `invoice:write`. |

Both flavours use the same UserInfo call — there's no separate
server-to-server endpoint to wire up.

## Requesting the right scopes

The `resource_access` block is gated by scope:

- Request `scope=roles` to receive the `roles` array per Audience.
- Request `scope=permissions` to receive the `permissions` array per
  Audience (bypass-pre-expanded + RS-subset-narrowed).
- Both standard scopes are seeded into every realm.

Without those scopes, the corresponding arrays are omitted (or
empty) and your gating will deny everyone — make sure the OAuth
client requests them.

## Common pitfalls

- **`GetClaimsFromUserInfoEndpoint = false`** (default in some
  templates) — UserInfo is never called, so `resource_access` never
  reaches the principal, so role/permission claims are missing, so
  `[Authorize(Roles)]` denies everyone. Always opt in.
- **Wrong `AppSlug` in `AddModgudClient`** — the transformation
  reads `resource_access[<wrong-slug>]`, finds nothing, doesn't add
  roles/permissions. Symptoms: authenticated user, no roles, no
  permissions. Double-check the slug matches the App in Modgud.
- **Resource server not linked to an App** — without a linked App
  there's no `PermissionIds` subset, so the `resource_access` block
  for this Audience is empty. Open the OAuth API in Modgud admin
  and assign the App.
- **Token's `aud` doesn't match `JwtBearerOptions.Audience`** — JWT
  validation rejects with audience mismatch. The convention is
  `aud == app-slug`; align both sides.
- **`scope=permissions` not requested** — `resource_access` carries
  roles but the permissions array stays empty. Add the scope to
  your client's allowed scopes and to the authorization request.

## Reference

- Concept overview: [Apps and resource_access](../concepts/apps-and-resource-access.md)
- Permissions reference: [Permissions & gating](../concepts/permissions.md)
- OAuth endpoints: [reference/oauth-api](../reference/oauth-api.md)
- Library source: `src/dotnet/Modgud.Client.AspNetCore/`
