namespace TimeToDo.Authorization.Events;

public record PermissionRoleCreatedEvent(
    Guid Id,
    string Name,
    string? Description,
    string ResourceType,
    List<string> Permissions);

public record PermissionRoleUpdatedEvent(
    Guid Id,
    string Name,
    string? Description,
    string ResourceType,
    List<string> Permissions);

public record PermissionRoleDeletedEvent(Guid Id);
