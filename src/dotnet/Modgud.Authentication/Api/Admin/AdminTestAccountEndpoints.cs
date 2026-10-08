using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.TestAccounts;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Authentication.Api.Admin;

public record SetTestAccountRequest(bool IsTestAccount);

public record SetFixedEmailCodeRequest(string Code, DateTimeOffset? ExpiresAt);

/// <summary>
/// ADR 0026 — test accounts in the admin console. Under <c>/api/admin</c>, so the realm's
/// administration level applies (ADR 0025), and gated on <c>user:test-account</c>: a
/// fixed code is a way into someone's account, so <c>user:write</c> alone is not enough.
/// <c>RequiresPermission</c> is cookie-only — no service account and no Management API
/// bearer token reaches these routes.
/// </summary>
public static class AdminTestAccountEndpoints
{
    public const string Permission = "user:test-account";

    public static WebApplication MapAdminTestAccountEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin")
            .WithTags("Admin Test Accounts")
            .RequireAuthorization()
            .RequiresPermission(Permission);

        // GET /api/admin/test-accounts — every test account of the realm, with the last use.
        group.MapGet("test-accounts", async (IQuerySession session, ITestAccountService service, CancellationToken ct) =>
        {
            var users = await session.Query<ApplicationUser>()
                .Where(u => u.IsTestAccount && !u.IsDeleted)
                .ToListAsync(ct);
            var rows = new List<object>(users.Count);
            foreach (var u in users.OrderBy(u => u.UserName))
            {
                var status = await service.GetStatusAsync(u.Id, ct);
                rows.Add(new
                {
                    Id = BuildingBlocks.Helper.ShortGuid.Encode(u.Id),
                    u.UserName,
                    u.Email,
                    u.IsActive,
                    status?.HasFixedEmailCode,
                    status?.FixedEmailCodeExpiresAt,
                    status?.FixedEmailCodeLastUsedAt,
                    status?.FixedEmailCodeLastUsedClientId,
                });
            }
            return Results.Ok(rows);
        })
        .WithName("Admin_TestAccounts_List");

        // GET /api/admin/users/{id}/test-account
        group.MapGet("users/{id}/test-account", async (string id, ITestAccountService service, CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(BuildingBlocks.Helper.ShortGuid.Decode(id), ct);
            return status is null ? Results.NotFound(new { Message = "User not found" }) : Results.Ok(status);
        })
        .WithName("Admin_TestAccount_Get");

        // PUT /api/admin/users/{id}/test-account { IsTestAccount }
        group.MapPut("users/{id}/test-account", async (
            string id, SetTestAccountRequest request, HttpContext http, ITestAccountService service, CancellationToken ct) =>
        {
            var result = await service.SetMarkerAsync(
                BuildingBlocks.Helper.ShortGuid.Decode(id), request.IsTestAccount, ActorId(http), ct);
            return ToResult(result);
        })
        .WithName("Admin_TestAccount_Set");

        // PUT /api/admin/users/{id}/test-account/fixed-email-code { Code, ExpiresAt }
        group.MapPut("users/{id}/test-account/fixed-email-code", async (
            string id, SetFixedEmailCodeRequest request, HttpContext http, ITestAccountService service, CancellationToken ct) =>
        {
            var result = await service.SetFixedEmailCodeAsync(
                BuildingBlocks.Helper.ShortGuid.Decode(id), request.Code ?? "", request.ExpiresAt, ActorId(http), ct);
            return ToResult(result);
        })
        .WithName("Admin_TestAccount_SetFixedEmailCode");

        // DELETE /api/admin/users/{id}/test-account/fixed-email-code
        group.MapDelete("users/{id}/test-account/fixed-email-code", async (
            string id, HttpContext http, ITestAccountService service, CancellationToken ct) =>
        {
            var result = await service.RemoveFixedEmailCodeAsync(
                BuildingBlocks.Helper.ShortGuid.Decode(id), ActorId(http), ct);
            return ToResult(result);
        })
        .WithName("Admin_TestAccount_RemoveFixedEmailCode");

        return application;
    }

    private static Guid? ActorId(HttpContext http) => http.GetUserId();

    private static IResult ToResult(ErrorOr<Success> result) => result.Match<IResult>(
        _ => Results.NoContent(),
        errors => errors[0].Type == ErrorType.NotFound
            ? Results.NotFound(new { Message = errors[0].Description })
            : Results.BadRequest(new { Error = errors[0].Code, Message = errors[0].Description }));
}
