using Marten;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authentication.AuthLog;

namespace Cocoar.Auth.Authentication.Api.Admin;

public static class AuthLogEndpoints
{
    public static WebApplication MapAuthLogEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/auth-log")
            .WithTags("Admin Auth Log")
            .RequireAuthorization();

        group.MapGet("", async (IQuerySession session, int? limit) =>
        {
            var entries = await session.Query<AuthLogDocument>()
                .OrderByDescending(x => x.Timestamp)
                .Take(limit ?? 200)
                .ToListAsync();

            return Results.Ok(entries);
        })
        .WithName("AdminAuthLog_Get")
        .RequiresPermission("auth-log:read");

        // Clearing the auth log is destructive — gate behind the global app:admin
        // bypass. (We deliberately don't add an `auth-log:write` since the only
        // write op is wipe-all.)
        group.MapDelete("", async (IDocumentSession session) =>
        {
            session.DeleteWhere<AuthLogDocument>(x => true);
            await session.SaveChangesAsync();
            return Results.Ok(new { Message = "Auth log cleared" });
        })
        .WithName("AdminAuthLog_Clear")
        .RequiresPermission("app:admin");

        return application;
    }
}
