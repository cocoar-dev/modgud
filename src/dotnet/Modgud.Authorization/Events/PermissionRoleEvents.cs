namespace Modgud.Authorization.Events;

public record PermissionRoleCreatedEvent(
    Guid Id,
    string Name,
    string? Description,
    Guid? AppId,
    bool IsRealmAdmin,
    List<Guid> PermissionIds);

public record PermissionRoleUpdatedEvent(
    Guid Id,
    string Name,
    string? Description,
    Guid? AppId,
    bool IsRealmAdmin,
    List<Guid> PermissionIds);

public record PermissionRoleDeletedEvent(Guid Id);
