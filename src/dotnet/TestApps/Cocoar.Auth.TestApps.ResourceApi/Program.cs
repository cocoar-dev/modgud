using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

// Disable the default JWT short→long claim translation so we see "sub", "name",
// "scope" verbatim instead of the System.IdentityModel ClaimTypes.* aliases.
// Resource servers built on top of Cocoar.Auth do the same in production.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// Cocoar.Auth.TestApps.ResourceApi — protected sample API.
//
// Validates JWTs issued by Cocoar.Auth (port 9099) and exposes endpoints
// at progressively stronger gates so we can see each authorization layer
// end-to-end:
//
//   GET /me            — any authenticated principal (echoes the claims +
//                        whatever the lib stamped onto it from the
//                        distribution-API call)
//   GET /scoped        — token-scope based gate (legacy: "demo.read")
//   GET /admin         — token-scope based gate (legacy: "demo.admin")
//   GET /policy/read   — RequiresCocoarPermission("demo:read")
//                        — distribution-API + 2-tier-eval
//   POST /policy/write — RequiresCocoarPermission("demo:write")
//                        — same path, different action; "demo:admin" or
//                        "realm:admin" cover this via the bypass tiers
//
// What the new path proves: the IdP knows nothing about the granular
// "demo:read"/"demo:write" permissions until the AppPermission catalog
// of the linked App is populated and a role grants the matching ids.
// The lib pulls that information per request from
// /api/v1/distribution/me-permissions, projects it onto the principal,
// and the endpoint filter checks it against the same evaluator the IdP
// uses internally — so resource:admin and realm:admin bypass behave
// identically on the RS side.

var builder = WebApplication.CreateBuilder(args);

// All knobs are env-/appsettings-configurable so the integration rig
// (Playwright, future Testcontainers harness) can point us at a
// dynamically allocated authority without recompiling.
var authority = builder.Configuration["TESTAPPS:AUTHORITY"] ?? "http://localhost:9099";
var audience = builder.Configuration["TESTAPPS:AUDIENCE"] ?? "demo-api";
var appSlug = builder.Configuration["TESTAPPS:APPSLUG"] ?? "cocoar-auth";
var rsId = builder.Configuration["TESTAPPS:RSID"] ?? "demo-api";
var rsSecret = builder.Configuration["TESTAPPS:RSSECRET"]
    ?? "demo-api-secret-please-rotate";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = false; // dev only
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
    });

// Wire the Cocoar.Auth helper lib: typed distribution-API client + 30s
// permission cache + per-request claims-transformation + the
// RequiresCocoarPermission endpoint filter. The lib forwards the user's
// bearer token from the incoming request and uses the configured
// RSId/RSSecret as RS-credentials when calling the distribution API.
builder.Services.AddCocoarAuthClient(o =>
{
    o.AppSlug = appSlug;
    o.IdpBaseUrl = authority;
    o.ResourceServerId = rsId;
    o.ResourceServerSecret = rsSecret;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("demo.read", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(HasScope("demo.read")));

    options.AddPolicy("demo.admin", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(HasScope("demo.admin")));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyHeader().AllowAnyMethod()
    .WithOrigins("http://localhost:7080", "http://localhost:5173")));

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    name = user.Identity?.Name,
    sub = user.FindFirstValue("sub"),
    scopes = user.FindAll("scope").Select(c => c.Value)
                  .Concat(user.FindAll("scp").Select(c => c.Value))
                  .ToArray(),
    // Roles + permissions + groups are stamped by CocoarAuthClaimsTransformation
    // from the distribution-API response. They will be empty if the IdP
    // hasn't been configured with a catalog + role for this user yet
    // (and that's the whole point of the catalog UI — to make this
    // first-class).
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
    permissions = user.FindAll(CocoarAuthClaimsTransformation.PermissionClaimType)
                       .Select(c => c.Value).ToArray(),
    groups = user.FindAll(CocoarAuthClaimsTransformation.GroupClaimType)
                  .Select(c => c.Value).ToArray(),
    claims = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
})).RequireAuthorization();

// Legacy scope-gated endpoints — kept for compatibility with the
// existing demo-seed (which provisions clients with demo.read /
// demo.admin scopes) and for end-to-end Playwright runs that haven't
// migrated yet.
app.MapGet("/scoped", () => Results.Ok(new { message = "You called the read-scoped endpoint." }))
   .RequireAuthorization("demo.read");

app.MapGet("/admin", () => Results.Ok(new { message = "You called the admin endpoint." }))
   .RequireAuthorization("demo.admin");

// Distribution-API / RequiresCocoarPermission gated endpoints — the
// post-Step-8 path. These exercise: incoming bearer → claims-
// transformation calls /api/v1/distribution/me-permissions with RS
// credentials → response cached for 30s and projected onto the
// principal → the filter reads the "permission" claims and runs them
// through Cocoar.Auth.Permissions.PermissionEvaluator (same evaluator
// the IdP uses).
app.MapGet("/policy/read", () => Results.Ok(new { message = "You called demo:read." }))
   .RequireAuthorization()
   .RequiresCocoarPermission("demo:read");

app.MapPost("/policy/write", () => Results.Ok(new { message = "You called demo:write." }))
   .RequireAuthorization()
   .RequiresCocoarPermission("demo:write");

app.Run();

static Func<AuthorizationHandlerContext, bool> HasScope(string required) =>
    ctx =>
    {
        // OpenIddict can emit either a single space-delimited "scope" claim,
        // a single "scp" claim, or one claim per scope — handle all three.
        var values = ctx.User.FindAll("scope").Concat(ctx.User.FindAll("scp"))
            .Select(c => c.Value);

        foreach (var v in values)
            foreach (var s in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(s, required, StringComparison.Ordinal))
                    return true;

        return false;
    };
