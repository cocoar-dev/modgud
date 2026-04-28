using Marten;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Authentication.Api.Admin;

/// <summary>
/// Request body for PUT /api/admin/users/{id}/grace/policy. All fields are optional;
/// null means "don't change". Use <c>GracePeriodDaysOverride = -1</c> to clear the
/// per-user override and fall back to the global <see cref="AppSettings.TwoFactorGracePeriodDays"/>.
/// </summary>
public record GracePolicyRequest(int? GracePeriodDaysOverride, bool? TwoFactorExempt);

/// <summary>
/// Admin-only operations for the 2FA grace period: reset a user's grace clock so they
/// get another full <see cref="AppSettings.TwoFactorGracePeriodDays"/> window, or clear
/// it entirely to force immediate enforcement on next login.
/// </summary>
public static class AdminGraceEndpoints
{
    public static WebApplication MapAdminGraceEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/users")
            .WithTags("Admin Grace Period")
            .RequireAuthorization()
            .RequiresPermission("app:admin");

        // GET /api/admin/users/{id}/security-info — Has2FA, 2FA methods, grace due date,
        // and per-user overrides (individual grace days + hard exempt flag).
        group.MapGet("{id}/security-info", async (
            string id,
            IDocumentSession session) =>
        {
            var userId = BuildingBlocks.Helper.ShortGuid.Decode(id);
            var user = await session.LoadAsync<ApplicationUser>(userId);
            if (user is null) return Results.NotFound(new { Message = "User not found" });

            var methods = await TwoFactorHelper.GetMethodsAsync(user, session);
            var securityData = await session.LoadAsync<UserSecurityData>(userId);

            return Results.Ok(new
            {
                Has2FA = methods.Count > 0,
                TwoFactorMethods = methods,
                SecureSetupDueAt = securityData?.SecureSetupDueAt,
                GracePeriodDaysOverride = securityData?.GracePeriodDaysOverride,
                TwoFactorExempt = securityData?.TwoFactorExempt ?? false,
            });
        })
        .WithName("Admin_UserSecurityInfo");

        // PUT /api/admin/users/{id}/grace/policy — set the per-user grace override and/or
        // the hard exempt flag. Null values in the request mean "don't change".
        group.MapPut("{id}/grace/policy", async (
            string id,
            GracePolicyRequest request,
            IDocumentSession session) =>
        {
            var userId = BuildingBlocks.Helper.ShortGuid.Decode(id);
            var user = await session.LoadAsync<ApplicationUser>(userId);
            if (user is null) return Results.NotFound(new { Message = "User not found" });

            var securityData = await session.LoadAsync<UserSecurityData>(userId)
                ?? UserSecurityData.Create(userId);

            if (request.GracePeriodDaysOverride is not null)
            {
                // Special sentinel "-1" means "clear override, fall back to global default".
                // Positive values set the per-user override; 0 means "immediate enforcement".
                securityData.GracePeriodDaysOverride = request.GracePeriodDaysOverride == -1
                    ? null
                    : Math.Max(0, request.GracePeriodDaysOverride.Value);
            }
            if (request.TwoFactorExempt is not null)
            {
                securityData.TwoFactorExempt = request.TwoFactorExempt.Value;
            }

            session.Store(securityData);
            await session.SaveChangesAsync();

            Serilog.Log.Information(
                "Admin: Grace policy updated. User={UserName} Override={Override} Exempt={Exempt}",
                user.UserName, securityData.GracePeriodDaysOverride, securityData.TwoFactorExempt);
            return Results.Ok(new
            {
                securityData.GracePeriodDaysOverride,
                securityData.TwoFactorExempt,
            });
        })
        .WithName("Admin_SetGracePolicy");

        // POST /api/admin/users/{id}/grace/reset — extend the user's grace period by the
        // configured number of days from now.
        group.MapPost("{id}/grace/reset", async (
            string id,
            IDocumentSession session,
            IAuthSettings settings) =>
        {
            var userId = BuildingBlocks.Helper.ShortGuid.Decode(id);
            var user = await session.LoadAsync<ApplicationUser>(userId);
            if (user is null) return Results.NotFound(new { Message = "User not found" });

            var securityData = await session.LoadAsync<UserSecurityData>(userId);
            if (securityData is null)
            {
                securityData = UserSecurityData.Create(userId);
            }

            // Use the per-user override if set, otherwise fall back to the global default.
            // A tenant-specific grace (e.g. 90 days for one user) is reset to its own length
            // rather than snapped back to the global 14.
            var graceDays = Math.Max(0, securityData.GracePeriodDaysOverride ?? settings.TwoFactorGracePeriodDays);
            securityData.SecureSetupDueAt = DateTime.UtcNow.AddDays(graceDays);
            session.Store(securityData);
            await session.SaveChangesAsync();

            Serilog.Log.Information("Admin: Grace period reset. User={UserName} DueAt={DueAt}",
                user.UserName, securityData.SecureSetupDueAt);
            return Results.Ok(new { SecureSetupDueAt = securityData.SecureSetupDueAt });
        })
        .WithName("Admin_ResetGracePeriod");

        // DELETE /api/admin/users/{id}/grace — expire the grace period immediately.
        // Sets DueAt to now, so the middleware and next login both treat it as past.
        // (Setting to null would cause the middleware to lazy-stamp a fresh grace — not
        // what "Force immediate enforcement" means.)
        group.MapDelete("{id}/grace", async (
            string id,
            IDocumentSession session) =>
        {
            var userId = BuildingBlocks.Helper.ShortGuid.Decode(id);
            var user = await session.LoadAsync<ApplicationUser>(userId);
            if (user is null) return Results.NotFound(new { Message = "User not found" });

            var securityData = await session.LoadAsync<UserSecurityData>(userId)
                ?? UserSecurityData.Create(userId);

            securityData.SecureSetupDueAt = DateTime.UtcNow;
            session.Store(securityData);
            await session.SaveChangesAsync();

            Serilog.Log.Information("Admin: Grace period expired immediately. User={UserName}", user.UserName);
            return Results.NoContent();
        })
        .WithName("Admin_ClearGracePeriod");

        return application;
    }
}
