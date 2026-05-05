using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Sessions;
using Cocoar.Auth.Authentication.Setup;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Authentication.Api.Account;

/// <summary>
/// First-admin bootstrap form for a freshly provisioned realm (C15).
/// Anonymous, rate-limited (the same "setup" policy that protected the
/// old <c>/api/setup/*</c> endpoints). Consumes a one-shot token from
/// <see cref="PendingAdminInvite"/> and creates the user + admin role +
/// group in a single Marten transaction via
/// <see cref="IRealmAdminBootstrapper"/>.
///
/// <para>The token's plaintext only ever reaches the recipient via the
/// magic-link in the bootstrap email (or via the CLI stdout in Dev /
/// air-gapped scenarios). This endpoint never echoes the token.</para>
///
/// <para>Mounted on the tenant realm's host — NOT CP-only. The whole
/// point of the invite mechanism is to give the *named recipient* a way
/// to onboard onto their tenant; the CP-gate would defeat that.</para>
/// </summary>
public static class BootstrapEndpoints
{
    public sealed record ConsumeInviteRequest(string Token, string Password);

    public static WebApplication MapBootstrapEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/account/bootstrap-admin")
            .WithTags("Bootstrap")
            .AllowAnonymous()
            // Same policy as the old /api/setup/* surface — 10 attempts
            // per 15 min per IP. Bootstrap is at most one click per
            // recipient; this is a brake on automated probing of leaked
            // tokens.
            .RequireRateLimiting("setup");

        group.MapPost("", async (
            ConsumeInviteRequest request,
            HttpContext http,
            IPendingAdminInviteService inviteService,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ISessionService sessionService) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var result = await inviteService.ConsumeAsync(request.Token, request.Password);
            if (result.IsError)
            {
                Serilog.Log.Information(
                    "Auth: Bootstrap-invite consume rejected. IP={IP} Code={Code}",
                    ip, result.FirstError.Code);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: result.FirstError.Code,
                    detail: result.FirstError.Description);
            }

            // Sign the new admin in immediately so they land on the
            // dashboard without having to re-type their just-set password.
            // SignInAsync goes through the full ApplicationScheme cookie
            // path — same posture as a regular login.
            var user = await userManager.FindByIdAsync(result.Value.UserId.ToString());
            if (user is not null)
            {
                await signInManager.SignInAsync(user, isPersistent: false);
                await SessionTracker.RecordLoginAsync(sessionService, http, user.Id);
            }

            Serilog.Log.Warning(
                "Auth: Bootstrap admin created via invite. IP={IP} UserName={UserName}",
                ip, result.Value.UserName);

            return Results.Ok(new { Message = "Bootstrap successful", UserName = result.Value.UserName });
        })
        .WithName("Bootstrap_ConsumeInvite");

        return app;
    }
}
