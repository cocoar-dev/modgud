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
// Validates JWTs issued by Cocoar.Auth (port 9099) and exposes three
// endpoints with progressively stronger gates so we can see each
// authorization layer end-to-end:
//
//   GET /me        — any authenticated principal (echoes the claims)
//   GET /scoped    — requires scope "demo.read"     (Cocoar.Auth scope)
//   GET /admin     — requires scope "demo.admin"
//
// Mirrors how a real Cocoar SaaS app would consume the IdP — the only
// thing it adds beyond stock JwtBearer is the claims-transformation
// from Cocoar.Auth.Client.AspNetCore (flat groups + role claims).

var builder = WebApplication.CreateBuilder(args);

// Configurable via TESTAPPS__* env vars or appsettings.json so the
// Playwright rig can point us at a dynamically allocated auth port.
var authority = builder.Configuration["TESTAPPS:AUTHORITY"] ?? "http://localhost:9099";
var audience = builder.Configuration["TESTAPPS:AUDIENCE"] ?? "demo-api";
var appSlug = builder.Configuration["TESTAPPS:APPSLUG"] ?? "cocoar-auth";

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

builder.Services.AddCocoarAuthClaimsTransformation(o =>
{
    // The flattening uses resource_access[<slug>].roles. For the demo we
    // run under cocoar-auth's own slug — switch this to the consuming
    // app's slug in real resource servers.
    o.AppSlug = appSlug;
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
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
    groups = user.FindAll("group").Select(c => c.Value).ToArray(),
    claims = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
})).RequireAuthorization();

app.MapGet("/scoped", () => Results.Ok(new { message = "You called the read-scoped endpoint." }))
   .RequireAuthorization("demo.read");

app.MapGet("/admin", () => Results.Ok(new { message = "You called the admin endpoint." }))
   .RequireAuthorization("demo.admin");

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
