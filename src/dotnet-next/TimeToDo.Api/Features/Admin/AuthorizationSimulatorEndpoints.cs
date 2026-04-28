using BuildingBlocks.Helper;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Infrastructure.AccessPolicy;

namespace TimeToDo.Api.Features.Admin;

/// <summary>
/// Admin diagnostic endpoint: "Would user X be allowed to do Y on row Z?"
/// Surfaces permission and scope trace so admins can understand unexpected 403s.
/// </summary>
public static class AuthorizationSimulatorEndpoints
{
    public record SimulateRequest(
        string UserId,
        string ResourceType,
        string Action,
        string? ResourceId);

    public static WebApplication MapAuthorizationSimulatorEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/authorization")
            .WithTags("Admin Authorization")
            .RequireAuthorization()
            .RequiresPermission("app:admin");

        group.MapPost("simulate", async (SimulateRequest req, IAuthorizationSimulator simulator) =>
        {
            if (!ShortGuid.TryDecode(req.UserId, out var userId))
                return Results.BadRequest(new { error = "Invalid UserId" });

            Guid? resourceId = null;
            if (!string.IsNullOrWhiteSpace(req.ResourceId))
            {
                if (!ShortGuid.TryDecode(req.ResourceId, out var parsed))
                    return Results.BadRequest(new { error = "Invalid ResourceId" });
                resourceId = parsed;
            }

            var result = await simulator.SimulateAsync(userId, req.ResourceType, resourceId, req.Action);
            return Results.Ok(result);
        }).WithName("Admin_Authorization_Simulate");

        return application;
    }
}
