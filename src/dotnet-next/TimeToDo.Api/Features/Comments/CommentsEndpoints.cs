using BuildingBlocks.Helper;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Api.Features.Comments.Commands;
using TimeToDo.Api.Features.Comments.Queries;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Infrastructure.AccessPolicy;
using Wolverine;

namespace TimeToDo.Api.Features.Comments;

public static class CommentsEndpoints
{
    public static WebApplication MapCommentsEndpoints(this WebApplication application, string path)
    {
        var commentGroup = application.MapGroup($"{path}/comment")
            .WithTags("Comments V2 (Marten)")
            .RequireAuthorization();

        // Get all comments
        commentGroup.MapGet("", async (HttpContext context, IMessageBus bus, string? id = null, string? orderBy = null, int? skip = null, int? take = null) =>
            {
                var userId = context.GetUserId();
                var query = new GetAllCommentsQuery(userId, id, orderBy, skip, take);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<CommentListDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_Comment_GetAll")
            .RequiresPermission("comment:read");

        // Get comments by reference ID and type
        commentGroup.MapGet("{type}/{referenceId}", async (HttpContext context, IMessageBus bus, string type, ShortGuid referenceId) =>
            {
                var userId = context.GetUserId();
                var query = new GetCommentsByReferenceQuery(referenceId.Guid, type, userId);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<CommentListDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_Comment_GetByReferenceId")
            .RequiresPermission("comment:read");

        // Get by ID
        commentGroup.MapGet("{id}", async (HttpContext context, ShortGuid id, IMessageBus bus) =>
            {
                var userId = context.GetUserId();
                var query = new GetCommentByIdQuery(id.Guid, userId);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<CommentListDto>>(query);
                return result.ToResult();
            })
            .WithName("V2_Comment_GetById")
            .RequiresPermission("comment:read");

        // Create comment
        commentGroup.MapPost("{type}/{referenceId}", async (
            HttpContext context,
            IMessageBus bus,
            IAccessPolicyEngine accessPolicy,
            string type,
            ShortGuid referenceId,
            CommentCreateDto createDto) =>
            {
                var userId = context.GetUserId();
                if (userId is null)
                    return Results.Unauthorized();

                // Verify user can access the referenced resource
                if (!await CanAccessReferencedItem(accessPolicy, userId.Value, type, referenceId.Guid))
                    return Results.Forbid();

                var command = new CreateCommentCommand(
                    createDto.Description,
                    referenceId.Guid,
                    type,
                    userId.Value);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<CommentListDto>>(command);
                return result.ToResult();
            })
            .WithName("V2_Comment_Create")
            .RequiresPermission("comment:create");

        // Mark as read
        commentGroup.MapPost("{id}/read", async (ShortGuid id, HttpContext context, IMessageBus bus) =>
            {
                var userId = context.GetUserId();
                if (userId is null)
                    return Results.Unauthorized();

                var command = new MarkCommentAsReadCommand(id.Guid, userId.Value);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Comment_MarkAsRead")
            .RequiresPermission("comment:read");

        // Delete comment
        commentGroup.MapDelete("{id}", async (ShortGuid id, HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy, Marten.IQuerySession session) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();

                // Load comment to get referenced item, then check access
                var comment = await session.LoadAsync<TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments.CommentView>(id.Guid);
                if (comment is null) return Results.NotFound();
                if (!await CanAccessReferencedItem(accessPolicy, userId.Value, comment.ReferencedItemType, comment.ReferencedItemId))
                    return Results.Forbid();

                var command = new DeleteCommentCommand(id.Guid, userId);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Comment_Delete")
            .RequiresPermission("comment:delete");

        return application;
    }

    /// <summary>
    /// Checks if the user can access the referenced resource (todo or customer).
    /// Scope is evaluated against groups granting read on the referenced resource type.
    /// </summary>
    private static async Task<bool> CanAccessReferencedItem(
        IAccessPolicyEngine accessPolicy, Guid userId, string type, Guid referenceId)
    {
        return type.ToLowerInvariant() switch
        {
            "todo" => await accessPolicy.CanAccessTodoForActionAsync(userId, referenceId, "todo:read"),
            "customer" => await accessPolicy.CanAccessCustomerForActionAsync(userId, referenceId, "customer:read"),
            _ => false // Unknown type = deny
        };
    }
}
