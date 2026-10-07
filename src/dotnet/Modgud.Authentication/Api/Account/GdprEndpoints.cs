using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Gdpr;
using Microsoft.AspNetCore.Authorization;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// GDPR self-service endpoints. Each endpoint operates on the caller's
/// own user id — there is no admin path here. The admin permanent-erase
/// lives on the user-management endpoints, gated by <c>gdpr:admin</c>.
/// </summary>
public static class GdprEndpoints
{
    public static WebApplication MapGdprEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/auth")
            .WithTags("GDPR")
            .RequireAuthorization();

        // GET /api/auth/export-data — Article 20 data dump
        group.MapGet("export-data", [Authorize] async (
            HttpContext context,
            IGdprService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.ExportUserDataAsync(userId.Value, ct);
            return result.IsError
                ? result.ToResult()
                : Results.Json(result.Value, contentType: "application/json", statusCode: 200);
        })
        .WithName("Auth_ExportData");

        // POST /api/auth/delete-account — Schedule a self-service deletion. The
        // account is erased at the grace deadline unless the user cancels first.
        group.MapPost("delete-account", [Authorize] async (
            RequestDeletionDto dto,
            HttpContext context,
            IGdprService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            // ADR 0025 amendment C — never blocked by a sign-in policy (the enforcement lets
            // it through), but it needs the same recent proof as every account change.
            var user = await context.RequestServices
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Modgud.Authentication.Domain.ApplicationUser>>()
                .GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            var accountChanges = context.RequestServices.GetRequiredService<IAccountChangeGuard>();
            if (await accountChanges.RequireRecentProofAsync(context, user, ct) is { } reauth) return reauth;

            var result = await svc.RequestDeletionAsync(userId.Value, dto.Password, dto.Reason, ct);
            if (!result.IsError)
                await accountChanges.NotifyAsync(user, AccountChange.DeletionRequested, ct);
            return result.ToResult();
        })
        .WithName("Auth_RequestDeletion");

        // POST /api/auth/cancel-deletion — Cancel the caller's own pending deletion
        group.MapPost("cancel-deletion", [Authorize] async (
            HttpContext context,
            IGdprService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.CancelDeletionAsync(userId.Value, cancelledByAdminUserId: null, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_CancelDeletion");

        // GET /api/auth/deletion-status — Pending / masked / clean
        group.MapGet("deletion-status", [Authorize] async (
            HttpContext context,
            IGdprService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.GetDeletionStatusAsync(userId.Value, ct);
            return result.ToResult();
        })
        .WithName("Auth_DeletionStatus");

        return application;
    }
}
