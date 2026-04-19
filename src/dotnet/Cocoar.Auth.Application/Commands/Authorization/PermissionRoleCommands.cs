using Cocoar.Auth.Application.DTOs.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using ErrorOr;
using Marten;
using Marten.Linq;

namespace Cocoar.Auth.Application.Commands.Authorization;

// ── Create ────────────────────────────────────────────────────────────

public record CreatePermissionRoleCommand(CreatePermissionRoleInput Input);

public class CreatePermissionRoleHandler(IDocumentSession session)
{
    public async Task<ErrorOr<PermissionRoleDto>> HandleAsync(
        CreatePermissionRoleCommand command, CancellationToken ct)
    {
        var input = command.Input;
        if (string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("PermissionRole.Name", "Name is required.");
        if (string.IsNullOrWhiteSpace(input.ResourceType))
            return Error.Validation("PermissionRole.ResourceType", "ResourceType is required.");

        var nameTaken = await session.Query<PermissionRole>()
            .AnyAsync(r => !r.IsDeleted && r.Name == input.Name, ct);
        if (nameTaken)
            return Error.Conflict("PermissionRole.Name", $"A permission role named '{input.Name}' already exists.");

        var id = Guid.CreateVersion7();
        session.Events.StartStream<PermissionRole>(id,
            new PermissionRoleCreatedEvent(
                Id: id,
                Name: input.Name,
                Description: input.Description,
                ResourceType: input.ResourceType,
                Permissions: input.Permissions ?? []));
        await session.SaveChangesAsync(ct);

        return new PermissionRoleDto
        {
            Id = id,
            Name = input.Name,
            Description = input.Description,
            ResourceType = input.ResourceType,
            Permissions = input.Permissions ?? [],
        };
    }
}

// ── Update ────────────────────────────────────────────────────────────

public record UpdatePermissionRoleCommand(Guid Id, UpdatePermissionRoleInput Input);

public class UpdatePermissionRoleHandler(IDocumentSession session)
{
    public async Task<ErrorOr<PermissionRoleDto>> HandleAsync(
        UpdatePermissionRoleCommand command, CancellationToken ct)
    {
        var current = await session.LoadAsync<PermissionRole>(command.Id, ct);
        if (current is null || current.IsDeleted)
            return Error.NotFound("PermissionRole.NotFound", $"PermissionRole {command.Id} not found.");

        var input = command.Input;
        if (string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("PermissionRole.Name", "Name is required.");

        var nameTaken = await session.Query<PermissionRole>()
            .AnyAsync(r => !r.IsDeleted && r.Id != command.Id && r.Name == input.Name, ct);
        if (nameTaken)
            return Error.Conflict("PermissionRole.Name", $"A permission role named '{input.Name}' already exists.");

        session.Events.Append(command.Id,
            new PermissionRoleUpdatedEvent(
                Id: command.Id,
                Name: input.Name,
                Description: input.Description,
                ResourceType: input.ResourceType,
                Permissions: input.Permissions ?? []));
        await session.SaveChangesAsync(ct);

        return new PermissionRoleDto
        {
            Id = command.Id,
            Name = input.Name,
            Description = input.Description,
            ResourceType = input.ResourceType,
            Permissions = input.Permissions ?? [],
        };
    }
}

// ── Delete ────────────────────────────────────────────────────────────

public record DeletePermissionRoleCommand(Guid Id);

public class DeletePermissionRoleHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Deleted>> HandleAsync(
        DeletePermissionRoleCommand command, CancellationToken ct)
    {
        var current = await session.LoadAsync<PermissionRole>(command.Id, ct);
        if (current is null || current.IsDeleted)
            return Error.NotFound("PermissionRole.NotFound", $"PermissionRole {command.Id} not found.");

        session.Events.Append(command.Id, new PermissionRoleDeletedEvent(command.Id));
        await session.SaveChangesAsync(ct);
        return Result.Deleted;
    }
}
