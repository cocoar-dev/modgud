using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Modgud.AspNetCore.ResourceServer;
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
//                        roles/permissions)
//   GET /scoped        — token-scope based gate ("demo.read")
//   GET /admin         — token-scope based gate ("demo.admin")
//   GET /policy/read   — RequireModgudPermission("demo:read")
//                        — exact-match against pre-expanded permissions
//   POST /policy/write — RequireModgudPermission("demo:write")
//
// What the path proves: the IdP issues a JWT with aud=<this-rs>, and the
// Modgud.AspNetCore.ResourceServer validates the token and projects its
// embedded resource_access[<aud>] =
// { permissions, roles } with bypass tiers (realm:admin, <r>:admin)
// already pre-expanded to concrete strings directly onto the identity.
// RequireModgudPermission then does straight membership match — no HTTP,
// no cache, no evaluator on the RS side.

var builder = WebApplication.CreateBuilder(args);

// All knobs are env-/appsettings-configurable so the integration rig
// (Playwright, future Testcontainers harness) can point us at a
// dynamically allocated authority without recompiling.
var authority = builder.Configuration["TESTAPPS:AUTHORITY"] ?? "http://localhost:9099";
var audience = builder.Configuration["TESTAPPS:AUDIENCE"] ?? "demo-api";

// TESTAPPS:TOKENMODE selects how this sample validates access tokens:
//   "jwt"        (default) — self-contained JWT validated locally against the
//                realm's JWKS.
//   "reference"  — Modgud's DEFAULT opaque token, validated per-request via
//                /connect/introspect. The RS
//                introspects with a confidential client whose client_id equals
//                its audience; supply its secret via TESTAPPS:INTROSPECTIONSECRET.
//   "both"       — accepts both formats under one public Modgud scheme and
//                dispatches by token shape.
// Everything downstream — the resource_access projection, RequireModgudPermission,
// role gates — is identical either way; only the registration differs.
var configuredTokenMode =
    (builder.Configuration["TESTAPPS:TOKENMODE"] ?? "jwt").Trim().ToLowerInvariant();
var tokenMode = configuredTokenMode switch
{
    "jwt" => ModgudTokenMode.OnlyJwt,
    "reference" => ModgudTokenMode.OnlyReferenceToken,
    "both" => ModgudTokenMode.Both,
    _ => throw new InvalidOperationException(
        "TESTAPPS:TOKENMODE must be 'jwt', 'reference', or 'both'."),
};

builder.Services.AddModgudResourceServer(options =>
{
    options.Authority = authority;
    options.Audience = audience;
    options.TokenMode = tokenMode;
    options.RequireHttpsMetadata = false; // dev only

    if (tokenMode is ModgudTokenMode.OnlyReferenceToken or ModgudTokenMode.Both)
    {
        options.IntrospectionClientSecret =
            builder.Configuration["TESTAPPS:INTROSPECTIONSECRET"];
    }

    if (tokenMode is ModgudTokenMode.OnlyJwt or ModgudTokenMode.Both)
    {
        options.ConfigureJwtBearer = jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.NameClaimType = "name";
            jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        };
    }
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
    // Roles + permissions come from the authentication scheme's audience-local
    // projection of resource_access[<audience>]. They will be empty
    // if the IdP hasn't emitted a block for this audience (e.g. because the
    // user has no grants in the linked App). Groups are never emitted by the
    // IdP (hub boundary, federation v1) — there is no "groups" key to read.
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
    permissions = user.FindAll(ModgudClaimTypes.Permission)
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
// incoming token → scheme-local resource_access projection → ASP.NET
// authorization policy does exact-match.
app.MapGet("/policy/read", () => Results.Ok(new { message = "You called demo:read." }))
   .RequireModgudPermission("demo:read");

app.MapPost("/policy/write", () => Results.Ok(new { message = "You called demo:write." }))
   .RequireModgudPermission("demo:write");

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
