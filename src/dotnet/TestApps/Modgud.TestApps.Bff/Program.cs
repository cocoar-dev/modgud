using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Yarp.ReverseProxy.Transforms;

// Modgud.TestApps.Bff — Backend-for-Frontend test rig.
//
// The browser holds a single httpOnly cookie. All tokens (access /
// refresh / id) live server-side in the auth ticket. A YARP reverse
// proxy under /api/* forwards calls to the ResourceApi and attaches
// the cached access token as a bearer header — the SPA never sees it.
//
// Endpoints:
//   GET  /bff/user      — current user (JSON) or 401
//   GET  /bff/login     — start OIDC challenge (top-level redirect)
//   GET  /bff/logout    — sign out locally + at the IdP
//   /api/{**catch-all}  — proxied to ResourceApi with bearer token
//
// CSRF: same-site Lax cookie + a required X-Requested-With header on
//       /bff/user and /api/*. Login/logout are top-level navigations
//       so they do not need the header.

var builder = WebApplication.CreateBuilder(args);

// All knobs are env-configurable so the Playwright rig (or a real
// deployment) can point them at dynamic hosts without recompiling.
var authority = builder.Configuration["TESTAPPS:AUTHORITY"] ?? "http://localhost:9099";
var resourceApi = builder.Configuration["TESTAPPS:RESOURCEAPI"] ?? "http://localhost:7081";
var clientId = builder.Configuration["TESTAPPS:CLIENTID"] ?? "demo-bff";
var clientSecret = builder.Configuration["TESTAPPS:CLIENTSECRET"] ?? "demo-bff-secret-please-rotate";

// Don't translate JWT claim names ("sub" → ClaimTypes.NameIdentifier etc.).
// Easier to debug and matches what the SPA wants to see.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "bff.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.RequireHttpsMetadata = false; // dev only
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("offline_access");
        // Authz-claim opt-ins per permission-modell: `roles` is the OIDC
        // standard convention for role names, `permissions` is Cocoar's
        // matching opt-in for the per-RS permission array. The BFF wants
        // both so /connect/userinfo emits a full resource_access block.
        options.Scope.Add("roles");
        options.Scope.Add("permissions");
        options.Scope.Add("demo.read");
        options.Scope.Add("demo.write");

        // Default "/signin-oidc" + "/signout-callback-oidc" — keep the
        // demo-seed.json redirect URIs aligned with these.
    });

builder.Services.AddAuthorization();

builder.Services.AddReverseProxy()
    .LoadFromMemory(
        new[]
        {
            new Yarp.ReverseProxy.Configuration.RouteConfig
            {
                RouteId = "resource-api",
                ClusterId = "resource-api",
                Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/api/{**catch-all}" },
                Transforms = new List<IReadOnlyDictionary<string, string>>
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/api" },
                },
            },
        },
        new[]
        {
            new Yarp.ReverseProxy.Configuration.ClusterConfig
            {
                ClusterId = "resource-api",
                Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>
                {
                    ["d1"] = new() { Address = resourceApi },
                },
            },
        })
    .AddTransforms(ctx =>
    {
        // Forward the cached access token to the resource server. The
        // SPA only ever sees its session cookie; the bearer token never
        // leaves the BFF process.
        ctx.AddRequestTransform(async transform =>
        {
            var token = await transform.HttpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(token))
            {
                transform.ProxyRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            // Strip the cookie — the upstream API doesn't need it and
            // we don't want it logged on the resource side.
            transform.ProxyRequest.Headers.Remove("Cookie");
        });
    });

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Light CSRF gate — refuse XHR-style endpoints unless the SPA explicitly
// opts in. Top-level navigations (login/logout) skip this guard.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var requiresXhr = path.StartsWithSegments("/api") ||
                      path.StartsWithSegments("/bff/user");
    if (requiresXhr && ctx.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Missing X-Requested-With header.");
        return;
    }
    await next();
});

app.MapGet("/bff/user", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        name = ctx.User.Identity.Name ?? ctx.User.FindFirstValue("name"),
        sub = ctx.User.FindFirstValue("sub"),
        email = ctx.User.FindFirstValue("email"),
        roles = ctx.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
        groups = ctx.User.FindAll("group").Select(c => c.Value).ToArray(),
    });
});

app.MapGet("/bff/login", (string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
        new[] { OpenIdConnectDefaults.AuthenticationScheme }));

app.MapGet("/bff/logout", () =>
    Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        new[]
        {
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme,
        }));

app.MapReverseProxy();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// Fallback to the SPA shell so client-side routing works on refresh.
app.MapFallbackToFile("index.html");

app.Run();
