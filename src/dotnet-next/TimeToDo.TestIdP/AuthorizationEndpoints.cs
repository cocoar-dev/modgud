using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.TestIdP;

/// <summary>
/// Authorize / Token / UserInfo endpoints. OpenIddict does the protocol
/// plumbing (code generation, token exchange, PKCE verification); we fill
/// in the user-identity half by pulling claims from the JSON config.
/// </summary>
public static class AuthorizationEndpoints
{
    public static void Map(WebApplication app)
    {
        // ─── /authorize ───────────────────────────────────────────
        app.MapMethods("/authorize", ["GET", "POST"],
            async (HttpContext http, TestIdpConfig config) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("No OIDC request on /authorize.");

            // Require a logged-in cookie session; redirect to /login otherwise.
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!auth.Succeeded)
            {
                var returnUrl = http.Request.Path + http.Request.QueryString;
                return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var subject = auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var user = config.Users.FirstOrDefault(u => u.Subject == subject);
            if (user is null)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login?error=userGone");
            }

            var identity = BuildUserIdentity(user, request.GetScopes());
            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            foreach (var claim in principal.Claims)
            {
                claim.SetDestinations(DestinationsFor(claim));
            }

            return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).AllowAnonymous();

        // ─── /token ───────────────────────────────────────────────
        app.MapPost("/token", async (HttpContext http, TestIdpConfig config) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("No OIDC request on /token.");

            TestIdpLog.Write(
                $"[Token] grant_type={request.GrantType} client_id={request.ClientId} redirect_uri={request.RedirectUri} code_len={request.Code?.Length ?? 0} code_verifier_len={request.CodeVerifier?.Length ?? 0}");

            if (!request.IsAuthorizationCodeGrantType())
                return Results.BadRequest(new { error = "unsupported_grant_type" });

            var authResult = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            TestIdpLog.Write($"[Token] authenticated={authResult.Succeeded} principal-claims={authResult.Principal?.Claims.Count() ?? 0} failure={authResult.Failure?.Message ?? "(none)"}");
            if (!authResult.Succeeded || authResult.Principal is null)
            {
                return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

            var subject = authResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? authResult.Principal.FindFirstValue(OpenIddictConstants.Claims.Subject);
            TestIdpLog.Write($"[Token] resolved subject={subject ?? "(null)"}");
            var user = config.Users.FirstOrDefault(u => u.Subject == subject);
            if (user is null)
            {
                TestIdpLog.Write($"[Token] user NOT FOUND for subject={subject}");
                return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

            var identity = BuildUserIdentity(user, authResult.Principal.GetScopes());
            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(authResult.Principal.GetScopes());
            foreach (var claim in principal.Claims)
            {
                claim.SetDestinations(DestinationsFor(claim));
            }

            TestIdpLog.Write($"[Token] signing in, claims={principal.Claims.Count()}, scopes={string.Join(",", principal.GetScopes())}");
            return Results.SignIn(principal, properties: null,
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).AllowAnonymous();

        // ─── /userinfo ────────────────────────────────────────────
        app.MapGet("/userinfo", async (HttpContext http, TestIdpConfig config) =>
        {
            var authResult = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            TestIdpLog.Write($"[UserInfo] authenticated={authResult.Succeeded} claims={authResult.Principal?.Claims.Count() ?? 0}");
            if (!authResult.Succeeded || authResult.Principal is null)
                return Results.Challenge(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

            // OpenIddict identity carries 'sub' as OpenIddictConstants.Claims.Subject
            // not ClaimTypes.NameIdentifier; check both.
            var subject = authResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? authResult.Principal.FindFirstValue(OpenIddictConstants.Claims.Subject);
            TestIdpLog.Write($"[UserInfo] resolved subject={subject ?? "(null)"}");
            var user = config.Users.FirstOrDefault(u => u.Subject == subject);
            if (user is null) return Results.NotFound();

            var payload = new Dictionary<string, object?> { ["sub"] = user.Subject };
            foreach (var (key, value) in user.Claims)
                payload[key] = value;
            return Results.Ok(payload);
        }).AllowAnonymous();
    }

    private static ClaimsIdentity BuildUserIdentity(TestIdpUser user, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, user.Subject));

        foreach (var (key, value) in user.Claims)
            AddClaim(identity, key, value);

        return identity;
    }

    /// <summary>
    /// Walk a claim value (which the JSON can serialize as string, number,
    /// array, or object) and emit individual Claim objects. Arrays become
    /// multi-valued claims — which is exactly what Entra/Okta do with groups
    /// and roles.
    /// </summary>
    private static void AddClaim(ClaimsIdentity identity, string type, object? value)
    {
        if (value is null) return;

        if (value is JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    identity.AddClaim(new Claim(type, element.GetString() ?? ""));
                    return;
                case JsonValueKind.Number:
                    identity.AddClaim(new Claim(type, element.GetRawText()));
                    return;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    identity.AddClaim(new Claim(type, element.GetBoolean().ToString().ToLowerInvariant()));
                    return;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        AddClaim(identity, type, item);
                    return;
                case JsonValueKind.Object:
                    identity.AddClaim(new Claim(type, element.GetRawText(), "json"));
                    return;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return;
            }
        }

        if (value is IEnumerable<object> list)
        {
            foreach (var v in list) AddClaim(identity, type, v);
            return;
        }

        identity.AddClaim(new Claim(type, value.ToString() ?? ""));
    }

    /// <summary>
    /// OpenIddict requires each claim to declare which token it should appear
    /// in. For a test IdP we're generous: everything goes into the ID token
    /// and is also surfaced via UserInfo, so the OIDC client sees the claims
    /// whether <c>GetClaimsFromUserInfoEndpoint</c> is on or off.
    /// </summary>
    private static IEnumerable<string> DestinationsFor(Claim claim)
    {
        yield return OpenIddictConstants.Destinations.AccessToken;
        if (claim.Type is OpenIddictConstants.Claims.Subject
            or OpenIddictConstants.Claims.Name
            or "email"
            or "preferred_username"
            or "groups"
            or "roles"
            or "amr"
            or "department")
        {
            yield return OpenIddictConstants.Destinations.IdentityToken;
        }
    }
}
