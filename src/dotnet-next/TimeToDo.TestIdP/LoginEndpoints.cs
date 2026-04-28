using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.TestIdP;

/// <summary>
/// The login UI — a single HTML page with a user-picker + password field.
/// Cookie-based, so once a user logs in they can complete multiple OIDC
/// authorizations without re-entering credentials (like a real IdP session).
/// </summary>
public static class LoginEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/login", (HttpContext http, TestIdpConfig config) =>
        {
            var returnUrl = http.Request.Query["returnUrl"].ToString();
            var error = http.Request.Query["error"].ToString();
            return Results.Content(LoginPage.Render(config, returnUrl, error), "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext http, TestIdpConfig config) =>
        {
            var form = await http.Request.ReadFormAsync();
            var userName = form["userName"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = config.Users.FirstOrDefault(u =>
                string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));

            if (user is null || user.Password != password)
            {
                var redirect = $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid";
                return Results.Redirect(redirect);
            }

            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Subject));
            identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
            var principal = new ClaimsPrincipal(identity);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            var destination = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
            return Results.Redirect(destination);
        }).AllowAnonymous();

        app.MapPost("/logout-session", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
    }
}

public static class LoginPage
{
    public static string Render(TestIdpConfig config, string returnUrl, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html><head><meta charset="utf-8"><title>TestIdP Login</title>
<style>
body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;background:#f3f4f6;margin:0;padding:40px;display:flex;justify-content:center}
.card{background:#fff;border-radius:8px;box-shadow:0 4px 12px rgba(0,0,0,.08);padding:28px;width:360px}
h1{font-size:1.2rem;margin:0 0 4px}
.subtitle{color:#6b7280;font-size:.85rem;margin:0 0 20px}
label{display:block;font-size:.8rem;color:#374151;margin:10px 0 4px;font-weight:500}
select,input{width:100%;padding:8px 10px;border:1px solid #d1d5db;border-radius:4px;font-size:.9rem;box-sizing:border-box}
button{width:100%;margin-top:16px;padding:10px;background:#2563eb;color:#fff;border:0;border-radius:4px;font-size:.9rem;cursor:pointer}
button:hover{background:#1d4ed8}
.hint{background:#fef3c7;border:1px solid #fcd34d;padding:8px 10px;border-radius:4px;font-size:.8rem;color:#78350f;margin-top:12px}
.err{background:#fef2f2;border:1px solid #fca5a5;padding:8px 10px;border-radius:4px;font-size:.85rem;color:#991b1b;margin-bottom:10px}
.footer{font-size:.75rem;color:#9ca3af;margin-top:20px;text-align:center}
</style></head><body><div class="card">
<h1>TestIdP</h1>
<p class="subtitle">Development-only identity provider for TimeToDo.</p>
""");

        if (!string.IsNullOrEmpty(error))
        {
            sb.Append($"<div class=\"err\">Login failed — check username and password.</div>");
        }

        sb.Append($"<form method=\"post\" action=\"/login\">");
        sb.Append($"<input type=\"hidden\" name=\"returnUrl\" value=\"{WebUtility.HtmlEncode(returnUrl)}\"/>");
        sb.Append("<label>User</label><select name=\"userName\">");
        foreach (var user in config.Users)
        {
            var email = user.Claims.TryGetValue("email", out var e) ? e?.ToString() : "";
            sb.Append($"<option value=\"{WebUtility.HtmlEncode(user.UserName)}\">{WebUtility.HtmlEncode(user.UserName)} — {WebUtility.HtmlEncode(email ?? "")}</option>");
        }
        sb.Append("</select>");
        sb.Append("<label>Password</label><input type=\"password\" name=\"password\" value=\"test123\" autocomplete=\"off\"/>");
        sb.Append("<button type=\"submit\">Sign in</button>");
        sb.Append("</form>");

        sb.Append("<div class=\"hint\">Default password for all seeded users: <code>test123</code></div>");
        sb.Append("<div class=\"footer\">TimeToDo.TestIdP · OpenIddict</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}

public static class HomePage
{
    public static string Render(TestIdpConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html><head><meta charset="utf-8"><title>TestIdP</title>
<style>
body{font-family:system-ui,sans-serif;background:#f3f4f6;margin:0;padding:40px;max-width:720px;margin:0 auto}
h1{margin:0 0 10px}
.info{background:#fff;border-radius:8px;padding:20px;box-shadow:0 2px 6px rgba(0,0,0,.06);margin-bottom:16px}
code{background:#f3f4f6;padding:2px 6px;border-radius:3px;font-size:.85em}
.list{margin:6px 0;padding-left:18px}
.muted{color:#6b7280;font-size:.9rem}
a{color:#2563eb}
</style></head><body>
<h1>TimeToDo.TestIdP</h1>
<p class="muted">OpenID Connect server for local TimeToDo development.</p>
""");

        sb.Append("<div class=\"info\"><h3>Discovery</h3>");
        sb.Append("<p><code>/.well-known/openid-configuration</code></p>");
        sb.Append("<p class=\"muted\">Point TimeToDo's Generic OIDC flavor at <code>http://localhost:5005/.well-known/openid-configuration</code>.</p></div>");

        sb.Append("<div class=\"info\"><h3>Registered clients</h3><ul class=\"list\">");
        foreach (var c in config.Clients)
        {
            sb.Append($"<li><b>{WebUtility.HtmlEncode(c.ClientId)}</b> — secret <code>{WebUtility.HtmlEncode(c.ClientSecret)}</code>");
            sb.Append("<br><span class=\"muted\">Allowed redirect URIs (prefix):</span><ul>");
            foreach (var r in c.RedirectUris) sb.Append($"<li><code>{WebUtility.HtmlEncode(r)}</code></li>");
            sb.Append("</ul></li>");
        }
        sb.Append("</ul></div>");

        sb.Append("<div class=\"info\"><h3>Seeded users</h3><ul class=\"list\">");
        foreach (var u in config.Users)
        {
            var email = u.Claims.TryGetValue("email", out var e) ? e?.ToString() : "";
            sb.Append($"<li><b>{WebUtility.HtmlEncode(u.UserName)}</b> — {WebUtility.HtmlEncode(email ?? "(no email)")}</li>");
        }
        sb.Append("</ul><p class=\"muted\">All passwords: <code>test123</code></p></div>");

        sb.Append("<p class=\"muted\">Edit <code>data/test-idp-config.json</code> (or the gitignored <code>.local.json</code>) and restart to change users/claims/clients.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
