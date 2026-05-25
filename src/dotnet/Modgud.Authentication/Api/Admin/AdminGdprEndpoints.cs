using BuildingBlocks.Helper;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Gdpr;
using Modgud.Authorization.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin-side GDPR operations — permanent erasure of user data with
/// audit reason. Soft-delete still goes through the regular user CRUD
/// path; this is the irreversible PII-masking flow.
/// </summary>
public static class AdminGdprEndpoints
{
    public static WebApplication MapAdminGdprEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/users")
            .WithTags("Admin GDPR")
            .RequireAuthorization();

        // DELETE /api/admin/users/{id}/permanent — irreversible PII erasure.
        // The audit reason travels in the body so the operator must explicitly
        // pass it; an empty body returns 400.
        group.MapDelete("{id}/permanent", async (
            string id,
            [FromBody] AdminPermanentEraseDto dto,
            HttpContext context,
            IGdprService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Reason))
                return Results.BadRequest(new { error = "Reason is required for permanent erasure." });

            var userId = ShortGuid.Decode(id);
            var adminUserId = context.GetUserId();
            var result = await svc.PermanentlyEraseAsync(userId, adminUserId, dto.Reason, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Admin_Gdpr_PermanentErase")
        .RequiresPermission("gdpr:admin");

        return application;
    }
}
