# Modgud.AspNetCore.ResourceServer

ASP.NET Core integration for APIs protected by a
[Modgud](https://github.com/cocoar-dev/modgud) identity provider.

The package has one registration method and one public authentication scheme.
`ModgudTokenMode` controls whether the API accepts self-contained JWTs, opaque
reference tokens, or both. In `Both` mode, the package routes three-part JWTs to
local validation and opaque tokens to RFC 7662 introspection.

Both validation paths select `resource_access[<audience>]` and project its roles
and permissions onto the authenticated identity. Roles use `ClaimTypes.Role`;
permissions use `ModgudClaimTypes.Permission`.

## Install

```bash
dotnet add package Modgud.AspNetCore.ResourceServer
```

## JWT quickstart

JWT is the default mode:

```csharp
using Modgud.AspNetCore.ResourceServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "event-tree-api";
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/admin/ping", () => "pong")
    .RequireAuthorization(policy => policy.RequireRole("Editor"));

app.MapPost("/calendars/{id}", (string id) => Results.Ok())
    .RequireModgudPermission("calendar:write");

app.Run();
```

JWT mode makes no per-request call to Modgud. A token must contain the
configured audience and its `resource_access` block. There is deliberately no
UserInfo fallback.

## Reference-token mode

```csharp
builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "event-tree-api";
    options.TokenMode = ModgudTokenMode.OnlyReferenceToken;
    options.IntrospectionClientSecret =
        builder.Configuration["Modgud:IntrospectionSecret"];
});
```

The resource server authenticates to `/connect/introspect` with a confidential
OAuth client. `IntrospectionClientId` defaults to `Audience`; in the usual setup
the introspection client's ID therefore equals the resource-server audience.
Validation is fail-closed and uncached, so revocation takes effect on the next
request.

## Accept both formats

```csharp
builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = "https://auth.example.com";
    options.Audience = "event-tree-api";
    options.TokenMode = ModgudTokenMode.Both;
    options.IntrospectionClientSecret =
        builder.Configuration["Modgud:IntrospectionSecret"];
});
```

The application still exposes one authentication scheme. Token shape only
selects the internal validator; it never bypasses signature, issuer, audience,
expiry, active-state, or DPoP validation. A second
`AddModgudResourceServer(...)` call is rejected.

## Permission gates

`RequireModgudPermission` adds normal ASP.NET Core authorization metadata. It
works on both `RouteHandlerBuilder` and `RouteGroupBuilder` and yields `401` for
anonymous callers or `403` for authenticated callers without the exact
permission:

```csharp
var writeApi = app.MapGroup("/write")
    .RequireModgudPermission("calendar:write");
```

Bypass grants such as `realm:admin` and `<resource>:admin` are expanded by the
IdP before token issuance. The resource server performs only an exact claim
check.

## Claims

Given:

```json
"resource_access": {
  "event-tree-api": {
    "roles": ["Editor"],
    "permissions": ["calendar:read", "calendar:write"]
  }
}
```

read the projected values with:

```csharp
var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value);
var permissions = user.FindAll(ModgudClaimTypes.Permission)
    .Select(claim => claim.Value);
```

## Session revocation

A JWT normally stays valid until `exp` even after the user signed out. With
`SessionRevocation` enabled the library follows the Application's change feed
with a management client and refuses tokens whose `sid` matches a session end
it has learned from the feed or recovery history.
Fail-open while the feed is unreachable; the denylist is bounded by the
access-token lifetime. See the integration guide for the Modgud-side setup
(feed enabled on the Application, a `client_credentials` client with
`modgud.management` and `app-scope:read`).

On startup and feed reset the SDK first reads
`/api/app/{id}/change-feed/session-revocations/snapshot`, merges bounded end
history, and then follows its checkpoint. Active or unknown session IDs are not
blocked merely because they are absent from a snapshot. Known denylist entries
survive retries and resets; recovery entries expire at the recorded end time plus
`AccessTokenLifetime + ClockSkew`, not at fetch time plus that window.

The configured lifetime must cover the longest JWT lifetime accepted for this
audience, including any relevant JWT-validation leeway. `ClockSkew` must be
nonnegative and their sum must be at most **seven days**. Server retention uses
the server clock; bounded clock differences remain an operational assumption.
Deploy the server before this SDK and allow at least that recovery window to
accumulate. Missing pre-upgrade ends cannot be reconstructed. An old server's
404 or an incomplete bootstrap response is a failed sync, not a successful empty
history; the worker retries without changing request-time fail-open behavior.

Recovery history is independent of feed queue compaction and generation changes.
App attribution uses the grant's published session state or its current
client/principal scope. It cannot recover an already removed historical App
association. It also does not make the existing net-projection live feed a
lossless event log, repair native/public session-ID correlation, or change other
revocation mechanisms. These are not universal immediate-logout guarantees.

```csharp
options.SessionRevocation = new ModgudSessionRevocationOptions
{
    Enabled = true,
    AppId = "<application id>",
    ClientId = "api-feed-reader",
    ClientSecret = "<secret>",
    AccessTokenLifetime = TimeSpan.FromMinutes(60),
};
```

## Configuration

| Option | Description |
| --- | --- |
| `Authority` | Required realm host root. |
| `Audience` | Required token audience and `resource_access` key. |
| `TokenMode` | `OnlyJwt` (default), `OnlyReferenceToken`, or `Both`. |
| `IntrospectionClientId` | Introspection client ID; defaults to `Audience`. |
| `IntrospectionClientSecret` | Required when the mode accepts reference tokens. |
| `RequireHttpsMetadata` | Requires an HTTPS authority; defaults to `true`. |
| `ConfigureJwtBearer` | Optional advanced JWT bearer configuration in JWT-capable modes. |
| `SessionRevocation` | Reject JWTs of sessions that ended before `exp`, learned from the Modgud Application change feed (`Enabled`, `AppId`, `ClientId`, `ClientSecret`, `AccessTokenLifetime`). Off by default; JWT-capable modes only. |

The valid option combination is checked immediately during registration.
`required` properties cannot express the mode-dependent secret requirement, so
invalid combinations fail with `OptionsValidationException`.

## License

Apache-2.0. See [LICENSE](https://github.com/cocoar-dev/modgud/blob/develop/LICENSE).
