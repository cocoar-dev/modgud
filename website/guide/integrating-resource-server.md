# Integrating a Resource Server

This guide walks through wiring a Cocoar SaaS app's backend (a "resource server" in OAuth-speak) to Cocoar.Auth so it can:

1. Validate access tokens that Cocoar.Auth issued
2. Pick up role claims via UserInfo so `[Authorize(Roles = "…")]` works
3. Optionally fetch granular permissions live via the distribution API

The reference scenario is a fictional `timetodo` app — replace the slug with yours throughout.

## Prerequisites

Before wiring code, finish the admin setup in Cocoar.Auth:

1. Create the app `timetodo` (with its resources)
2. Create an OAuth client (e.g. `timetodo-web`) and link it to `timetodo`
3. Click **Create default resource server** in the app detail — copy the one-time API secret
4. Set up at least one role + group with `BoundTo: ["timetodo"]` and assign your test user

The end-to-end userdoc walkthrough is at `/userdocs/saas-anbindung` (German).

## ASP.NET Core integration

### 1. Add the package reference

Until the NuGet package ships, reference the project from the Cocoar.Auth source tree:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
  <ProjectReference Include="..\..\cocoar.auth\src\dotnet\Cocoar.Auth.Client.AspNetCore\Cocoar.Auth.Client.AspNetCore.csproj" />
</ItemGroup>
```

### 2. Configure authentication

```csharp
using Cocoar.Auth.Client.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Your realm's issuer — the slug after /<host>/ is the realm slug.
        options.Authority = "https://auth.cocoar.dev/system";

        // Your app's slug. Cocoar.Auth uses the app slug as the audience
        // claim, so all microservices under one app share the same `aud`.
        options.Audience = "timetodo";

        // CRITICAL: pulls UserInfo on every token validation and merges its
        // claims into the principal. Without this, role claims sit on the
        // server and your endpoints don't see them.
        options.GetClaimsFromUserInfoEndpoint = true;
    });

// Flattens resource_access["timetodo"].roles into ClaimTypes.Role so
// [Authorize(Roles = "Editor")] just works. Also flattens the optional
// "groups" claim into a "group" claim type.
services.AddCocoarAuthClaimsTransformation(o =>
{
    o.AppSlug = "timetodo";
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

// Admin-only.
app.MapDelete("/admin/wipe", () => Results.Ok())
   .RequireAuthorization(p => p.RequireRole("Editor"));
```

That's it for the standard role-based path. If your app needs nothing more granular than role checks, you can stop here.

## Granular permissions: the distribution API

Roles like "Editor" are coarse. If your endpoint logic asks "does this user have `timetodo:todo:write`?" — that level of detail isn't in the access token. Cocoar.Auth exposes it via the **distribution API**, which you call live (and cache for ~30 seconds).

### Anatomy of the call

```http
GET /api/v1/distribution/me-permissions HTTP/1.1
Host: auth.cocoar.dev
Authorization: Bearer <user-access-token>
X-Resource-Server-Id: timetodo
X-Resource-Server-Secret: <api-secret-from-the-Klick-Aktion>
```

Two auth axes:

- **Bearer** (the user's access token) — answers "who is this user?"
- **`X-Resource-Server-*` headers** — answers "which resource server is asking?". The credentials come from the OAuth API admin (or the Klick-Aktion in App detail).

The **app context is derived from the resource server** — no `?app=` query is needed. The user's `X-Resource-Server-Id` is `timetodo`, so the IDP responds with permissions for `timetodo`.

### Response

```json
{
  "UserId": "abc123…",
  "AppSlug": "timetodo",
  "Permissions": [
    "timetodo:todo:read",
    "timetodo:todo:write",
    "timetodo:project:read"
  ],
  "Groups": [
    { "Id": "…", "Name": "TimeToDo Team" }
  ],
  "Roles": [
    { "Id": "…", "Name": "Editor" }
  ]
}
```

The response carries `Cache-Control: private, max-age=30`. You may cache per-user for 30 seconds; after that, refetch. That bounds the staleness window for permission revocation.

### Sample C# client

```csharp
public sealed class CocoarPermissionsClient
{
    private readonly HttpClient _http;
    private readonly string _appSlug;
    private readonly string _apiSecret;
    private readonly IMemoryCache _cache;

    public CocoarPermissionsClient(
        HttpClient http,
        IConfiguration config,
        IMemoryCache cache)
    {
        _http      = http;
        _appSlug   = config["Cocoar:AppSlug"]!;
        _apiSecret = config["Cocoar:ApiSecret"]!;
        _cache     = cache;
    }

    public async Task<MePermissionsDto> GetMyPermissionsAsync(
        string userBearerToken, CancellationToken ct = default)
    {
        // Cache key includes a hash of the bearer so a user-switch in the
        // same process invalidates correctly.
        var cacheKey = $"perms:{Hash(userBearerToken)}";
        if (_cache.TryGetValue(cacheKey, out MePermissionsDto? cached) && cached is not null)
            return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/distribution/me-permissions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userBearerToken);
        req.Headers.Add("X-Resource-Server-Id", _appSlug);
        req.Headers.Add("X-Resource-Server-Secret", _apiSecret);

        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var dto = await res.Content.ReadFromJsonAsync<MePermissionsDto>(cancellationToken: ct)
                  ?? throw new InvalidOperationException("Empty response from distribution API.");

        _cache.Set(cacheKey, dto, TimeSpan.FromSeconds(30));
        return dto;
    }

    private static string Hash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
    }
}

public sealed record MePermissionsDto(
    string UserId,
    string AppSlug,
    string[] Permissions,
    GroupRef[] Groups,
    RoleRef[] Roles);

public sealed record GroupRef(string Id, string Name);
public sealed record RoleRef(string Id, string Name);
```

### Wire it into an authorization policy

Use the response inside an authorization handler so endpoints can ask permission-level questions cleanly:

```csharp
public sealed class HasPermissionRequirement : IAuthorizationRequirement
{
    public HasPermissionRequirement(string permission) => Permission = permission;
    public string Permission { get; }
}

public sealed class HasPermissionHandler : AuthorizationHandler<HasPermissionRequirement>
{
    private readonly CocoarPermissionsClient _client;
    private readonly IHttpContextAccessor _http;

    public HasPermissionHandler(CocoarPermissionsClient client, IHttpContextAccessor http)
    {
        _client = client;
        _http   = http;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, HasPermissionRequirement req)
    {
        var bearer = _http.HttpContext?.Request.Headers.Authorization
            .ToString().Replace("Bearer ", "");
        if (string.IsNullOrEmpty(bearer)) return;

        var perms = await _client.GetMyPermissionsAsync(bearer);
        if (perms.Permissions.Contains(req.Permission))
            ctx.Succeed(req);
    }
}

// Registration:
services.AddSingleton<IAuthorizationHandler, HasPermissionHandler>();
services.AddAuthorization(o =>
    o.AddPolicy("CanWriteTodos", p =>
        p.AddRequirements(new HasPermissionRequirement("timetodo:todo:write"))));

// Use:
app.MapPost("/todos", (TodoDto dto) => Results.Ok())
   .RequireAuthorization("CanWriteTodos");
```

## Two auth flavours, when to pick which

| You need | Use |
| --- | --- |
| Coarse role gating (`Admin`, `Editor`, `Viewer`) | `[Authorize(Roles = "…")]` via UserInfo + claims-transformation. Zero per-request HTTP cost. |
| Granular per-action permissions (`todo:write`, `report:export`) | Distribution API + 30 s cache. One extra HTTP hop per user per ~30 s. |
| Both | Both. They compose — the same user can be `Roles="Editor"` *and* hold permission `timetodo:todo:write`. |

## Common pitfalls

- **`GetClaimsFromUserInfoEndpoint = false`** (default in some templates) — UserInfo is never called, so `resource_access` never reaches the principal, so role claims are missing, so `[Authorize(Roles)]` denies everyone. Always opt in.
- **Wrong `AppSlug` in `AddCocoarAuthClaimsTransformation`** — the transformation reads `resource_access[<wrong-slug>]`, finds nothing, doesn't add roles. Symptoms: authenticated user, no roles. Double-check the slug matches the App in cocoar.auth.
- **Resource server not linked to an App** — distribution API returns 400 `ResourceServerUnassigned`. Open the OAuth API in cocoar.auth admin and pick an App.
- **Token's `aud` doesn't match `JwtBearerOptions.Audience`** — JWT validation rejects with audience mismatch. The convention is `aud == app-slug`; align both sides.
- **Distribution API call without `X-Resource-Server-*` headers** — 401 with `WWW-Authenticate: CocoarAuthRS`. Add the headers; they're required on `/distribution/*` (not on `/me/*`, which is cookie-only).

## Reference

- Concept overview: [Apps and resource_access](../concepts/apps-and-resource-access.md)
- Distribution API spec: [reference/distribution-api](../reference/distribution-api.md)
- Library source: `src/dotnet/Cocoar.Auth.Client.AspNetCore/`
