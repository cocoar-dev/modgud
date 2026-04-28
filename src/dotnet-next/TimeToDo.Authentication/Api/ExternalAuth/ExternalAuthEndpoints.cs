using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using TimeToDo.Authentication.Domain.ExternalAuth;

namespace TimeToDo.Authentication.Api.ExternalAuth;

public static class ExternalAuthEndpoints
{
    public static void MapExternalAuthEndpoints(this IEndpointRouteBuilder endpoints, string path)
    {
        // Public — login page needs the list to render buttons. Only returns
        // enabled, non-deleted configs.
        endpoints.MapGet($"{path}/account/external-logins",
            async ([FromServices] IQuerySession session, CancellationToken ct) =>
            {
                var configs = await session.Query<IdpConfig>()
                    .Where(c => !c.IsDeleted && c.Enabled)
                    .ToListAsync(ct);

                return Results.Ok(configs.Select(c => new ExternalLoginDto(
                    Id: c.Id,
                    DisplayName: c.DisplayName,
                    Flavor: c.Flavor,
                    IconName: c.IconName,
                    ButtonColorHex: c.ButtonColorHex)).ToArray());
            }).AllowAnonymous();

        // Start login flow — issues OIDC challenge to the IdP. Redirects to
        // the IdP's authorize endpoint. Returns 404 if the config is not
        // enabled (no silent enumeration).
        endpoints.MapGet($"{path}/account/external-login/{{idpConfigId:guid}}/start",
            async (Guid idpConfigId,
                   string? returnUrl,
                   HttpContext http,
                   [FromServices] IQuerySession session,
                   CancellationToken ct) =>
            {
                var config = await session.LoadAsync<IdpConfig>(idpConfigId, ct);
                if (config is null || config.IsDeleted || !config.Enabled)
                    return Results.NotFound();

                var schemeName = DynamicOidcSchemeManager.SchemeNameFor(idpConfigId);
                // Return URL lives in Items so the finish endpoint honors it
                // AFTER processing; RedirectUri itself must point at finish
                // because OIDC handler redirects there straight out of the
                // callback with the External cookie already set.
                var props = new AuthenticationProperties
                {
                    RedirectUri = "/api/account/external-login/finish",
                    Items =
                    {
                        ["idpConfigId"] = idpConfigId.ToString("N"),
                        ["returnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl!,
                    },
                };
                return Results.Challenge(props, [schemeName]);
            }).AllowAnonymous();

        // RP-initiated logout: after TimeToDo's local cookie is already cleared
        // (by /account/logout), this endpoint redirects the browser to the IdP's
        // end_session_endpoint. OIDC middleware builds the URL (includes
        // post_logout_redirect_uri + id_token_hint when available).
        endpoints.MapGet($"{path}/account/external-logout/{{idpConfigId:guid}}",
            async (Guid idpConfigId, HttpContext http) =>
            {
                var schemeName = DynamicOidcSchemeManager.SchemeNameFor(idpConfigId);
                var props = new AuthenticationProperties { RedirectUri = "/login" };
                return Results.SignOut(props, [schemeName]);
            }).AllowAnonymous();

        // Finish endpoint: runs after OIDC middleware drops the ticket into
        // the External cookie. Processes the external principal (user
        // matching, JIT creation, claim-snapshot persistence) and issues the
        // Identity.Application cookie. Anonymous because the caller has no
        // app cookie yet; the External cookie is the gate.
        endpoints.MapGet($"{path}/account/external-login/finish",
            async (HttpContext http,
                   [FromServices] ExternalLoginProcessor processor,
                   [FromServices] Microsoft.AspNetCore.Identity.SignInManager<TimeToDo.Authentication.Domain.ApplicationUser> signInManager,
                   CancellationToken ct) =>
            {
                var auth = await http.AuthenticateAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);
                if (!auth.Succeeded || auth.Principal is null)
                    return Results.Redirect("/login?error=oidc-no-ticket");

                if (!auth.Properties!.Items.TryGetValue("idpConfigId", out var idpConfigIdValue)
                    || !Guid.TryParseExact(idpConfigIdValue, "N", out var idpConfigId))
                {
                    return Results.Redirect("/login?error=oidc-no-idp");
                }

                // If the app cookie is already present, this is a link-flow
                // initiated from Profile → Security. Pass the authenticated
                // user id down so the processor binds the external identity
                // to that account instead of JIT-creating a new one.
                var existingAuth = await http.AuthenticateAsync(
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);
                Guid? authenticatedUserId = null;
                if (existingAuth.Succeeded && existingAuth.Principal is not null)
                {
                    var idClaim = existingAuth.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (Guid.TryParse(idClaim, out var parsed)) authenticatedUserId = parsed;
                }

                var result = await processor.ProcessAsync(auth.Principal, idpConfigId, ct, authenticatedUserId);
                if (!result.Succeeded)
                {
                    await http.SignOutAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);
                    var code = Uri.EscapeDataString(result.ErrorCode ?? "unknown");
                    return Results.Redirect($"/login?error={code}");
                }

                // Sign in with the app cookie. Persistent=true gives the OIDC
                // path the same 30-day sliding lifetime as Passkey/Magic-Link.
                await http.SignInAsync(
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme,
                    result.Principal!,
                    new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });

                // Discard the short-lived External ticket now that we have
                // the application cookie — defense against stale claim-replay.
                await http.SignOutAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);

                var returnUrl = auth.Properties.Items.TryGetValue("returnUrl", out var ru) && !string.IsNullOrWhiteSpace(ru)
                    ? ru
                    : "/";
                return Results.Redirect(returnUrl);
            }).AllowAnonymous();
    }

    public record ExternalLoginDto(
        Guid Id,
        string DisplayName,
        string Flavor,
        string? IconName,
        string? ButtonColorHex);
}
