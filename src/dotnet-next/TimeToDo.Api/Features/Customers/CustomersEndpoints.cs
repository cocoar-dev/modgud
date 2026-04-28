using Marten;
using Microsoft.AspNetCore.Mvc;
using BuildingBlocks.Helper;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Api.Features.Customers.Commands;
using TimeToDo.Api.Features.Customers.Queries;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using Wolverine;

namespace TimeToDo.Api.Features.Customers;

public static class CustomersEndpoints
{
    public static WebApplication MapCustomersEndpoints(this WebApplication application, string path)
    {
        var customerGroup = application.MapGroup($"{path}/customer")
            .WithTags("Customers V2 (Marten)")
            .RequireAuthorization();

        // Get all customers (including archived to match v1 behavior)
        customerGroup.MapGet("", async (HttpContext context, IMessageBus bus, int? skip = null, int? take = null) =>
            {
                var userId = context.GetUserId();
                var query = new GetAllCustomersQuery(UserId: userId, IncludeArchived: true, Skip: skip, Take: take);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<CustomerListDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_Customer_GetAll")
            .RequiresPermission("customer:read");

        // Lightweight customer list for dropdowns (Id + Label), scoped to customer:read
        customerGroup.MapGet("lookup", async (HttpContext context, IQuerySession session, IAccessPolicyEngine accessPolicy) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();

                var filter = await accessPolicy.BuildCustomerFilterForActionAsync(userId.Value, "customer:read");
                var query = session.Query<CustomerView>().Where(c => !c.IsDeleted && !c.IsArchived);
                if (filter is not null) query = query.Where(filter);
                var customers = await query.ToListAsync();

                return Results.Ok(customers
                    .OrderBy(c => c.Name)
                    .Select(c => new
                    {
                        Id = new ShortGuid(c.Id).ToString(),
                        Label = c.Name,
                    }));
            })
            .WithName("V2_Customer_Lookup");

        // Get archived customers (must be before {id} route)
        customerGroup.MapGet("archived", async (HttpContext context, IMessageBus bus) =>
            {
                var userId = context.GetUserId();
                var query = new GetArchivedCustomersQuery(userId);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<CustomerListDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_Customer_GetArchived")
            .RequiresPermission("customer:read");

        // Get by ID (must be after specific routes like "archived")
        customerGroup.MapGet("{id}", async (ShortGuid id, HttpContext context, IMessageBus bus) =>
            {
                var userId = context.GetUserId();
                var query = new GetCustomerByIdQuery(id.Guid, userId);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<CustomerDto>>(query);
                return result.ToResult();
            })
            .WithName("V2_Customer_GetById")
            .RequiresPermission("customer:read");

        customerGroup.MapPost("", async (HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy, IAccessProtoBuilder protoBuilder, CustomerCreateDto createDto) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();

                var proto = protoBuilder.BuildCustomerProto(createDto.Name, createDto.Important);
                if (!await accessPolicy.CanCreateCustomerAsync(userId.Value, proto))
                    return Results.Forbid();

                var command = new CreateCustomerCommand(createDto.Name, createDto.Important);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<CustomerListDto>>(command);
                return result.ToResult(dto =>
                {
                    dto.Status = EntityStatus.Pending;
                    return Results.Ok(dto);
                });
            })
            .WithName("V2_Customer_Create")
            .RequiresPermission("customer:create");

        customerGroup.MapPut("{id}", async (ShortGuid id, HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy, CustomerUpdateDto dto) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, id.Guid, "customer:update")) return Results.Forbid();
                var command = new UpdateCustomerCommand(id.Guid, dto.Name, dto.Important, dto.IsArchived);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<CustomerListDto>>(command);
                return result.ToResult(dto =>
                {
                    dto.Status = EntityStatus.Pending;
                    return Results.Ok(dto);
                });
            })
            .WithName("V2_Customer_Update")
            .RequiresPermission("customer:update");

        // Archive/restore using PUT to match frontend expectations and Todo API
        // Frontend BaseEntityService uses: PUT /customer/archive and PUT /customer/archive?restore=true
        customerGroup.MapPut("archive", async (HttpContext context, [FromBody] List<string> ids, IMessageBus bus, IAccessPolicyEngine accessPolicy, bool restore = false) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                var guidIds = ids.Select(id => new ShortGuid(id).Guid).ToList();
                var actionPermission = restore ? "customer:restore" : "customer:archive";
                foreach (var custId in guidIds)
                    if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, custId, actionPermission)) return Results.Forbid();
                var command = new ArchiveCustomersCommand(guidIds, restore);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Customer_ArchiveRestore")
            .RequiresPermission("customer:archive");

        // Keep single-item POST endpoints for backward compatibility with v1
        customerGroup.MapPost("archive/{id}", async (ShortGuid id, HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, id.Guid, "customer:archive")) return Results.Forbid();
                var command = new ArchiveCustomersCommand(new List<Guid> { id.Guid }, Restore: false);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Customer_ArchiveById")
            .RequiresPermission("customer:archive");

        customerGroup.MapPost("restore/{id}", async (ShortGuid id, HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, id.Guid, "customer:restore")) return Results.Forbid();
                var command = new ArchiveCustomersCommand(new List<Guid> { id.Guid }, Restore: true);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Customer_RestoreById")
            .RequiresPermission("customer:restore");

        customerGroup.MapDelete("{id}", async (string id, HttpContext context, IMessageBus bus, IAccessPolicyEngine accessPolicy) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                var guid = new ShortGuid(id).Guid;
                if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, guid, "customer:delete")) return Results.Forbid();
                var command = new DeleteCustomersCommand(new List<Guid> { guid });
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Customer_DeleteSingle")
            .RequiresPermission("customer:delete");

        customerGroup.MapDelete("", async (HttpContext context, [FromBody] List<string> ids, IMessageBus bus, IAccessPolicyEngine accessPolicy) =>
            {
                var userId = context.GetUserId();
                if (userId is null) return Results.Unauthorized();
                var guids = ids.Select(id => new ShortGuid(id).Guid).ToList();
                foreach (var custId in guids)
                    if (!await accessPolicy.CanAccessCustomerForActionAsync(userId.Value, custId, "customer:delete")) return Results.Forbid();
                var command = new DeleteCustomersCommand(guids);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_Customer_Delete")
            .RequiresPermission("customer:delete");

        return application;
    }
}
