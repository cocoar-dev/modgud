using Microsoft.AspNetCore.Mvc;
using BuildingBlocks.Helper;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Api.Features.Todos.Commands;
using TimeToDo.Api.Features.Todos.Queries;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.AccessPolicy;
using Wolverine;

namespace TimeToDo.Api.Features.Todos;

public static class TodosEndpoints
{
    public static WebApplication MapTodosEndpoints(this WebApplication application, string path)
    {
        var todoGroup = application.MapGroup($"{path}/todo")
            .WithTags("Todos V2 (Marten)")
            .RequireAuthorization();

        todoGroup.MapGet("", GetAllTodos)
            .WithName("V2_Todo_GetAll")
            .RequiresPermission("todo:read");

        todoGroup.MapGet("archive", GetArchivedTodos)
            .WithName("V2_Todo_GetArchived")
            .RequiresPermission("todo:read");

        todoGroup.MapGet("{id}", GetTodoById)
            .WithName("V2_Todo_GetById")
            .RequiresPermission("todo:read");

        todoGroup.MapPost("", CreateTodo)
            .WithName("V2_Todo_Create")
            .RequiresPermission("todo:create");

        todoGroup.MapPut("{id}", UpdateTodo)
            .WithName("V2_Todo_Update")
            .RequiresPermission("todo:update");

        todoGroup.MapPut("update/status", UpdateStatus)
            .WithName("V2_Todo_UpdateStatus")
            .RequiresPermission("todo:update");

        todoGroup.MapPatch("update/flags", UpdateFlags)
            .WithName("V2_Todo_UpdateFlags")
            .RequiresPermission("todo:flag");

        todoGroup.MapPost("{subTodoId}/move-into/{parentTodoId}", MoveToParent)
            .WithName("V2_Todo_MoveToParent")
            .RequiresPermission("todo:move");

        todoGroup.MapPost("convert-to-parent", ConvertToParent)
            .WithName("V2_Todo_ConvertToParent")
            .RequiresPermission("todo:move");

        todoGroup.MapPut("archive", ArchiveTodos)
            .WithName("V2_Todo_Archive")
            .RequiresPermission("todo:archive");

        todoGroup.MapDelete("", DeleteTodos)
            .WithName("V2_Todo_Delete")
            .RequiresPermission("todo:delete");

        return application;
    }

    private static async Task<IResult> GetAllTodos(
        HttpContext context,
        IMessageBus bus,
        string? id = null,
        string? orderBy = null,
        int? skip = null,
        int? take = null)
    {
        var userId = context.GetUserId();
        var query = new GetAllTodosQuery(userId, id, orderBy, skip, take);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<TodoDto>>>(query);
        return result.ToResult();
    }

    private static async Task<IResult> GetArchivedTodos(
        HttpContext context,
        IMessageBus bus)
    {
        var userId = context.GetUserId();
        var query = new GetArchivedTodosQuery(userId);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<TodoDto>>>(query);
        return result.ToResult();
    }

    private static async Task<IResult> GetTodoById(
        ShortGuid id,
        HttpContext context,
        IMessageBus bus)
    {
        var userId = context.GetUserId();
        var query = new GetTodoByIdQuery(id.Guid, userId);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<TodoDto>>(query);
        return result.ToResult();
    }

    private static async Task<IResult> CreateTodo(
        HttpContext context,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy,
        IAccessProtoBuilder protoBuilder,
        [FromQuery] ShortGuid? parentTodo,
        TodoCreateDto createDto)
    {
        var userId = context.GetUserId();
        if (userId is null)
            return Results.Unauthorized();

        var responsibleUserIds = createDto.Responsibles?
            .Select(r => ShortGuid.Decode(r.Id))
            .ToList() ?? new List<Guid>();

        var customerId = createDto.Customer?.Id != null
            ? ShortGuid.Decode(createDto.Customer.Id)
            : (Guid?)null;

        // Proto-scope check: evaluate the "todo:create" filter against a pre-persist
        // view of the todo so access scripts (e.g. "customer must be X") gate the create.
        var proto = await protoBuilder.BuildTodoProtoAsync(
            createDto.Title, createDto.Description, createDto.DueDate, createDto.Status,
            customerId, responsibleUserIds, createDto.Critical, createDto.AwaitingFeedback,
            parentTodo?.Guid, userId.Value);
        if (!await accessPolicy.CanCreateTodoAsync(userId.Value, proto))
            return Results.Forbid();

        var command = new CreateTodoCommand(
            createDto.Title,
            createDto.Description,
            createDto.DueDate,
            createDto.Status,
            customerId,
            responsibleUserIds,
            createDto.Critical,
            createDto.AwaitingFeedback,
            parentTodo?.Guid,
            userId.Value);

        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<TodoDto>>(command);
        return result.ToResult(dto =>
        {
            dto.EntityStatus = EntityStatus.Pending;
            return Results.Ok(dto);
        });
    }

    private static async Task<IResult> UpdateTodo(
        ShortGuid id,
        HttpContext context,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy,
        TodoUpdateDto dto)
    {
        var userId = context.GetUserId();
        if (userId is null)
            return Results.Unauthorized();
        if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, id.Guid, "todo:update"))
            return Results.Forbid();

        var responsibleUserIds = dto.Responsibles.HasValue
            ? new Optional<List<Guid>>(dto.Responsibles.Value?
                .Select(r => ShortGuid.Decode(r.Id))
                .ToList() ?? new List<Guid>())
            : Optional<List<Guid>>.None;

        var customerId = dto.Customer.HasValue
            ? new Optional<Guid?>(dto.Customer.Value?.Id != null
                ? ShortGuid.Decode(dto.Customer.Value.Id)
                : null)
            : Optional<Guid?>.None;

        var command = new UpdateTodoCommand(
            id.Guid,
            dto.Title,
            dto.Description,
            dto.DueDate,
            dto.Status,
            customerId,
            responsibleUserIds,
            dto.Critical,
            dto.AwaitingFeedback,
            userId.Value);

        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<TodoDto>>(command);
        return result.ToResult(dto =>
        {
            dto.EntityStatus = EntityStatus.Pending;
            return Results.Ok(dto);
        });
    }

    private static async Task<IResult> UpdateStatus(
        HttpContext context,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy,
        TodoStatusUpdateRequestDto statusRequestDto)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var guidIds = statusRequestDto.Ids.Select(id => new ShortGuid(id).Guid).ToList();
        foreach (var todoId in guidIds)
            if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, todoId, "todo:update"))
                return Results.Forbid();
        var command = new UpdateTodoStatusCommand(guidIds, statusRequestDto.Status, userId);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }

    private static async Task<IResult> UpdateFlags(
        HttpContext context,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy,
        TodoFlagsUpdateRequestDto request)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var guidIds = request.Ids.Select(id => new ShortGuid(id).Guid).ToList();
        foreach (var todoId in guidIds)
            if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, todoId, "todo:flag"))
                return Results.Forbid();
        var command = new UpdateTodoFlagsCommand(guidIds, request.AddFlags, request.RemoveFlags, userId);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }

    private static async Task<IResult> MoveToParent(
        HttpContext context,
        ShortGuid subTodoId,
        ShortGuid parentTodoId,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();
        if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, subTodoId.Guid, "todo:move"))
            return Results.Forbid();
        if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, parentTodoId.Guid, "todo:move"))
            return Results.Forbid();

        var command = new MoveToParentCommand(subTodoId.Guid, parentTodoId.Guid);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }

    private static async Task<IResult> ConvertToParent(
        HttpContext context,
        List<string> ids,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var guidIds = ids.Select(ShortGuid.Decode).ToList();
        foreach (var todoId in guidIds)
            if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, todoId, "todo:move"))
                return Results.Forbid();
        var command = new ConvertToParentCommand(guidIds);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }

    private static async Task<IResult> ArchiveTodos(
        HttpContext context,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy,
        List<string> ids,
        bool restore = false)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var guidIds = ids.Select(ShortGuid.Decode).ToList();
        var actionPermission = restore ? "todo:restore" : "todo:archive";
        foreach (var todoId in guidIds)
            if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, todoId, actionPermission))
                return Results.Forbid();
        var command = new ArchiveTodosCommand(guidIds, restore);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }

    private static async Task<IResult> DeleteTodos(
        HttpContext context,
        [FromBody] List<string> ids,
        IMessageBus bus,
        IAccessPolicyEngine accessPolicy)
    {
        var userId = context.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var guidIds = ids.Select(ShortGuid.Decode).ToList();
        foreach (var todoId in guidIds)
            if (!await accessPolicy.CanAccessTodoForActionAsync(userId.Value, todoId, "todo:delete"))
                return Results.Forbid();
        var command = new DeleteTodosCommand(guidIds);
        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
        return result.ToNoContentResult();
    }
}
