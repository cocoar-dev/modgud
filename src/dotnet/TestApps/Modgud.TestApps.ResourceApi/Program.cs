using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Modgud.Client.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

// Disable the default JWT short→long claim translation so we see "sub", "name",
// "scope" verbatim instead of the System.IdentityModel ClaimTypes.* aliases.
// Resource servers built on top of Modgud do the same in production.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// Modgud.TestApps.ResourceApi — protected sample API.
//
// Validates JWTs issued by Modgud (port 9099) and exposes endpoints
// at progressively stronger gates so we can see each authorization layer
// end-to-end:
//
//   GET /me            — any authenticated principal (echoes claims +
//                        the resource_access[<audience>]-flattened
//                        roles/permissions/groups)
//   GET /scoped        — token-scope based gate ("demo.read")
//   GET /admin         — token-scope based gate ("demo.admin")
//   GET /policy/read   — RequiresCocoarPermission("demo:read")
//                        — exact-match against pre-expanded permissions
//   POST /policy/write — RequiresCocoarPermission("demo:write")
//
// What the path proves: the IdP issues a JWT with aud=<this-rs>, the
// JwtBearer middleware fetches /connect/userinfo (because we set
// GetClaimsFromUserInfoEndpoint=true), UserInfo emits
// resource_access[<aud>] = { permissions, roles, groups } with bypass
// tiers (realm:admin, <r>:admin) already pre-expanded to concrete
// strings, and the lib's claims-transformation flattens that block onto
// the principal. RequiresCocoarPermission then does straight membership
// match — no HTTP, no cache, no evaluator on the RS side.

var builder = WebApplication.CreateBuilder(args);

// All knobs are env-/appsettings-configurable so the integration rig
// (Playwright, future Testcontainers harness) can point us at a
// dynamically allocated authority without recompiling.
var authority = builder.Configuration["TESTAPPS:AUTHORITY"] ?? "http://localhost:9099";
var audience = builder.Configuration["TESTAPPS:AUDIENCE"] ?? "demo-api";

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

// Lib hooks JwtBearer.OnTokenValidated to fetch /connect/userinfo and
// merge resource_access onto the principal, then a ClaimsTransformation
// flattens the matching audience block to ClaimTypes.Role / "permission"
// / "group" claims. Plus the RequiresCocoarPermission endpoint filter.
builder.Services.AddModgudClient(o =>
{
    o.Authority = authority;
    o.Audience  = audience;  // must match JwtBearerOptions.Audience above
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
    // Roles + permissions + groups come from the lib's claims-transformation,
    // which read resource_access[<audience>] off the principal. They will
    // be empty if the IdP hasn't emitted a block for this audience (e.g.
    // because the user has no grants in the linked App).
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
    permissions = user.FindAll(ModgudClaimsTransformation.PermissionClaimType)
                       .Select(c => c.Value).ToArray(),
    groups = user.FindAll(ModgudClaimsTransformation.GroupClaimType)
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

// Permission-gated endpoints — the post-Step-7-fix path. These exercise:
// incoming bearer → JwtBearer fetches UserInfo → resource_access block
// projected onto principal → filter does exact-match.
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
